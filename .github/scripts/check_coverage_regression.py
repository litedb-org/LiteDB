"""Coverage-regression check: coverage may change, but never silently.

Compares a base and head revision and reports removed tests, new skips, fewer
assertions, changed fixtures/compatibility harnesses, fuzz corpus repins, new
expected-failure allowlist entries, unscheduled fuzz targets, weakened evidence
registries and changed CI test selection/timeouts. Every finding needs a
disposition in .github/safety/coverage-ledger.json that is added by the same
change, so an old entry never approves a later weakening.
"""
import argparse
import re
import sys
import traceback
from datetime import date

import safety_common as common

LEDGER = f"{common.SAFETY_DIR}/coverage-ledger.json"
CORPUS = "LiteDB.Fuzz/Corpus/regressions.json"
KNOWN_FINDINGS = "LiteDB.Fuzz/Corpus/known-findings.json"
FAULT_POINTS = f"{common.SAFETY_DIR}/fault-points.json"
CONTRACTS = f"{common.SAFETY_DIR}/contracts.json"
PROOFS = f"{common.SAFETY_DIR}/regression-proofs.json"
FIXTURES = [common.glob_regex(pattern) for pattern in (
    "LiteDB.Tests/Resources/**", "LiteDB.Tests/**/*.zip", "LiteDB.Tests/**/*.db")]
HARNESSES = [common.glob_regex(pattern) for pattern in (
    "scripts/test-*.py", "tools/*Compatibility/**", "tools/IndexMigrationFixture/**",
    "tools/IndexMigrationRecovery/**", "tools/V8Differential/**")]
TEST_SETTINGS = {"tests.runsettings", "tests.ci.runsettings", "LiteDB.Tests/xunit.runner.json",
                 ".github/os-matrix.json", f"{common.SAFETY_DIR}/ci-evidence.json"}
CI_SENSITIVE = re.compile(
    r"timeout-minutes|continue-on-error|if-no-files-found|TestCaseFilter|FullyQualifiedName"
    r"|--filter|-Filter\b|--list-tests|Required CI guard|\$guard|PartitionSuite", re.I)
ASSERTION = re.compile(r"\.Should\w*\(|\bAssert\.\w+\(")
KINDS = {
    "test-removed", "skip-added", "conditional-attribute", "assertions-reduced", "fixture-changed",
    "harness-changed", "corpus-case-removed", "corpus-repinned", "expected-failure-added",
    "fuzz-target-removed", "fuzz-target-unscheduled", "fault-evidence-removed",
    "contract-evidence-removed", "proof-guard-removed", "ci-test-config-changed"}
DISPOSITIONS = {
    "replaced": True, "relocated": True, "covered-elsewhere": True,
    "intentional-change": False, "obsolete": False, "strengthened": False}


class Finding:
    def __init__(self, kind, subject, detail):
        self.kind, self.subject, self.detail = kind, subject, detail

    def key(self):
        return self.kind, self.subject


def test_findings(base, head):
    before, after = common.collect_tests(base), common.collect_tests(head)
    by_name, added_in = {}, {}
    for fqn, (path, method) in after.items():
        by_name.setdefault(method.name, []).append(fqn)
        if fqn not in before:
            added_in.setdefault(path, []).append(method.name)
    findings = []
    for fqn, (path, method) in sorted(before.items()):
        if fqn not in after:
            moved = [other for other in by_name.get(method.name, []) if other not in before]
            note = f"; a test with this name was added as {', '.join(moved)}" if moved else ""
            if not moved and added_in.get(path):  # most often a rename
                note = f"; new in that file: {', '.join(sorted(added_in[path]))}"
            findings.append(Finding("test-removed", fqn, f"was in {path}{note}"))
    for fqn, (path, method) in sorted(after.items()):
        previous = before.get(fqn)
        old_skips = previous[1].skipped_variants if previous else 0
        if method.skipped_variants > old_skips:
            reasons = "; ".join(skip for skip in method.skips if skip)
            findings.append(Finding("skip-added", fqn, f"{path}: {reasons}"))
        if previous:
            added = set(method.kinds) - set(previous[1].kinds) - {"Fact", "Theory"}
            if added:
                findings.append(Finding("conditional-attribute", fqn, f"{path}: now [{', '.join(sorted(added))}]"))
    return findings


def assertion_findings(base, head, changes):
    """Fewer assertion calls in a kept test file; removed tests are reported separately."""
    remaining = set(common.collect_tests(head))
    findings = []
    for path, status in sorted(changes.items()):
        if status != "M" or not common.is_test_path(path):
            continue
        old = common.blank_code(base.read(path) or "")
        for method in common.parse_tests(base.read(path) or "").values():
            if method.fqn not in remaining:
                old = old[:method.start] + " " * (method.end - method.start) + old[method.end:]
        before = len(ASSERTION.findall(old))
        after = len(ASSERTION.findall(common.blank_code(head.read(path) or "")))
        if after < before:
            findings.append(Finding("assertions-reduced", path, f"{before} -> {after} assertion calls"))
    return findings


def file_findings(changes):
    findings = []
    for path, status in sorted(changes.items()):
        if status not in "MD":
            continue
        action = "deleted" if status == "D" else "modified"
        if any(pattern.match(path) for pattern in FIXTURES):
            findings.append(Finding("fixture-changed", path, f"existing fixture {action}"))
        elif any(pattern.match(path) for pattern in HARNESSES):
            findings.append(Finding("harness-changed", path, f"test or compatibility harness {action}"))
        elif path in TEST_SETTINGS:
            findings.append(Finding("ci-test-config-changed", path, f"test settings {action}"))
    return findings


def ci_line_findings(base, head, changes):
    paths = [path for path, status in changes.items() if status in "MD" and (
        path == "scripts/run-ci-tests.ps1"
        or (path.startswith(common.WORKFLOWS_DIR) and path.endswith((".yml", ".yaml"))))]
    findings = []
    for path in sorted(paths):
        old, new = (tree.read(path) or "" for tree in (base, head))
        removed = [line.strip() for line in _only_in(old, new) if CI_SENSITIVE.search(line)]
        added = [line.strip() for line in _only_in(new, old) if CI_SENSITIVE.search(line)]
        if path == "scripts/run-ci-tests.ps1":
            removed += [line.strip() for line in _only_in(_groups(old), _groups(new))]
            added += [line.strip() for line in _only_in(_groups(new), _groups(old))]
        if removed or added:
            detail = "; ".join([f"-{line}" for line in removed] + [f"+{line}" for line in added])
            findings.append(Finding("ci-test-config-changed", path, detail[:500]))
    return findings


def _only_in(text, other):
    remaining = other.splitlines()
    result = []
    for line in text.splitlines():
        if line in remaining:
            remaining.remove(line)
        else:
            result.append(line)
    return result


def _groups(script):
    match = re.search(r"\$groups\s*=\s*\[ordered\]@\{(.*?)\n\s*\}", script, re.S)
    return match.group(1) if match else ""


def corpus_findings(base, head):
    findings = []
    old = {_case_key(case): case for case in base.read_json(CORPUS, {}).get("cases", [])}
    new = {_case_key(case): case for case in head.read_json(CORPUS, {}).get("cases", [])}
    for key, case in sorted(old.items()):
        if key not in new:
            findings.append(Finding("corpus-case-removed", key, case.get("reason", "")))
        elif any(case.get(name) != new[key].get(name) for name in ("inputHash", "traceHash")):
            findings.append(Finding("corpus-repinned", key, "inputHash/traceHash changed"))
    old_known = {_finding_key(entry): entry for entry in base.read_json(KNOWN_FINDINGS, {}).get("findings", [])}
    for entry in head.read_json(KNOWN_FINDINGS, {}).get("findings", []):
        previous = old_known.get(_finding_key(entry), {})
        if entry.get("status") in ("known", "expected") and previous.get("status") != entry.get("status"):
            findings.append(Finding("expected-failure-added", _finding_key(entry),
                                    f"status {previous.get('status', 'absent')} -> {entry.get('status')}"))
    return findings


def _case_key(case):
    return f"{case.get('target')}:{case.get('seed')}:{case.get('count')}"


def _finding_key(entry):
    return f"{entry.get('target')}:{entry.get('fingerprint')}"


def fuzz_findings(base, head):
    findings = []
    old_targets, new_targets = common.fuzz_targets(base), common.fuzz_targets(head)
    findings += [Finding("fuzz-target-removed", name, old_targets[name])
                 for name in sorted(set(old_targets) - set(new_targets))]
    unscheduled = common.scheduled_fuzz_targets(base) - common.scheduled_fuzz_targets(head)
    findings += [Finding("fuzz-target-unscheduled", name, f"no longer in {common.FUZZ_WORKFLOW}")
                 for name in sorted(unscheduled & set(new_targets))]
    return findings


def registry_findings(base, head):
    findings = []
    for path, kind, entries, key in (
            (FAULT_POINTS, "fault-evidence-removed", "hooks", lambda item: f"{item['family']}:{item['name']}"),
            (FAULT_POINTS, "fault-evidence-removed", "injectors", lambda item: f"injector:{item['name']}"),
            (CONTRACTS, "contract-evidence-removed", "contracts", lambda item: item["id"])):
        old = {key(item): item for item in base.read_json(path, {}).get(entries, [])}
        new = {key(item): item for item in head.read_json(path, {}).get(entries, [])}
        for subject, item in sorted(old.items()):
            kept = {_canonical(evidence) for evidence in new.get(subject, {}).get("evidence", [])}
            lost = [evidence for evidence in item.get("evidence", []) if _canonical(evidence) not in kept]
            if lost:
                findings.append(Finding(kind, subject, "; ".join(_describe(evidence) for evidence in lost)))
    return findings


def proof_findings(base, head):
    """A regression proof's permanent guard may move, but not silently shrink."""
    old = {item.get("repro"): item for item in (base.read_json(PROOFS, {}) or {}).get("proofs", [])}
    new = {item.get("repro"): item for item in (head.read_json(PROOFS, {}) or {}).get("proofs", [])}
    findings = []
    for repro, item in sorted(old.items(), key=lambda pair: str(pair[0])):
        lost = sorted(set(item.get("permanentGuard", [])) - set(new.get(repro, {}).get("permanentGuard", [])))
        if lost:
            findings.append(Finding("proof-guard-removed", str(repro), "; ".join(lost)))
    return findings


def _canonical(evidence):
    return tuple(evidence.get(name) for name in ("test", "fuzz", "script"))


def _describe(evidence):
    if evidence.get("script"):
        return evidence["script"]
    return evidence.get("test") or f"fuzz:{evidence.get('fuzz')}"


def check_ledger(base, head, findings, report):
    """Match findings to dispositions added by this change; validate the ledger."""
    ledger = head.read_json(LEDGER, {}) or {}
    dispositions = common.section(ledger, "dispositions", list, report, LEDGER)
    old = base.read_json(LEDGER, {}) or {}
    old_ids = {str(entry.get("id")) for entry in old.get("dispositions", []) if isinstance(entry, dict)} \
        if isinstance(old, dict) and isinstance(old.get("dispositions"), list) else set()
    new_entries = [entry for entry in dispositions if str(entry.get("id")) not in old_ids]
    ids = [str(entry.get("id")) for entry in dispositions]
    for duplicate in sorted({value for value in ids if ids.count(value) > 1}):
        report.error(f"Duplicate disposition id {duplicate!r} in {LEDGER}", LEDGER)
    for entry in new_entries:
        validate_disposition(head, entry, report)
    covered = {(entry.get("kind"), subject) for entry in new_entries for subject in entry.get("subjects", [])}
    unresolved = [finding for finding in findings if finding.key() not in covered]
    for finding in unresolved:
        report.error(f"{finding.kind}: {finding.subject} ({finding.detail}) has no disposition added in this change")
    used = {finding.key() for finding in findings}
    for kind, subject in sorted(covered - used):
        report.warning(f"Disposition for {kind}: {subject} matches no change in this diff")
    validate_quarantine(head, common.section(ledger, "quarantine", list, report, LEDGER), report)
    return unresolved


def validate_disposition(head, entry, report):
    label = entry.get("id") or "<missing id>"
    for name in ("subjects", "coveredBy"):
        if not isinstance(entry.get(name, []), list):
            report.error(f"Disposition {label}: {name} must be a JSON array", LEDGER)
            entry[name] = []
    if not entry.get("id") or entry.get("kind") not in KINDS or not entry.get("subjects"):
        report.error(f"Disposition {label}: needs id, a known kind ({', '.join(sorted(KINDS))}) and subjects", LEDGER)
    disposition = entry.get("disposition")
    if disposition not in DISPOSITIONS:
        report.error(f"Disposition {label}: disposition must be one of {', '.join(DISPOSITIONS)}", LEDGER)
    for name in ("invariant", "reason"):
        if len((entry.get(name) or "").strip()) < 10:
            report.error(f"Disposition {label}: explain the {name} (at least a sentence)", LEDGER)
    if DISPOSITIONS.get(disposition) and not entry.get("coveredBy"):
        report.error(f"Disposition {label}: '{disposition}' must name where the invariant stays covered", LEDGER)
    if disposition == "intentional-change" and not entry.get("decision"):
        report.error(f"Disposition {label}: an intentional change must link its compatibility decision", LEDGER)
    for reference in entry.get("coveredBy", []):
        if not resolves(head, reference):
            report.error(f"Disposition {label}: coveredBy reference {reference!r} does not resolve", LEDGER)


def resolves(tree, reference):
    if reference.startswith("fuzz:"):
        return reference[5:] in common.fuzz_targets(tree)
    if "#" in reference:
        return common.resolve_test(tree, reference) is not None
    return tree.read(reference.split("#")[0]) is not None


QUARANTINE_FIELDS = ("test", "reason", "owner", "review", "gap", "issue", "expires")
ISSUE = re.compile(r"https://github\.com/[\w.-]+/[\w.-]+/issues/\d+\Z|(?:[\w.-]+/[\w.-]+)?#\d+\Z")


def validate_quarantine(head, entries, report, today=None):
    """Every quarantine entry names its test, owner, reason, gap, a linked issue, a review date and an
    expiry date (review <= expires). A passed date is a warning here (it must not fail unrelated PRs);
    the weekly quarantine re-run (quarantine_rerun.py) reports it as a failure."""
    tests = common.collect_tests(head)
    today = today or date.today()
    seen = set()
    for entry in entries:
        test = entry.get("test")
        if test in seen:
            report.error(f"Quarantine lists {test} twice", LEDGER)
        seen.add(test)
        missing = [name for name in QUARANTINE_FIELDS if not entry.get(name)]
        if missing:
            report.error(f"Quarantine entry {test or '<missing test>'} lacks {', '.join(missing)}", LEDGER)
        if test and test not in tests:
            report.error(f"Quarantined test {test} no longer exists; remove its entry", LEDGER)
        if entry.get("issue") and not ISSUE.match(str(entry["issue"])):
            report.error(f"Quarantine entry {test}: issue must link a GitHub issue (URL, #n or owner/name#n)", LEDGER)
        dates = {}
        for name in ("review", "expires"):
            try:
                dates[name] = date.fromisoformat(entry[name]) if entry.get(name) else None
            except (TypeError, ValueError):
                report.error(f"Quarantine entry {test}: {name} must be an ISO date", LEDGER)
                dates[name] = None
        if dates["review"] and dates["expires"] and dates["review"] > dates["expires"]:
            report.error(f"Quarantine entry {test}: the review date must not be after the expiry date", LEDGER)
        if dates["expires"] and dates["expires"] < today:
            report.warning(f"Quarantine of {test} expired on {entry['expires']} (owner {entry.get('owner')}, "
                           f"{entry.get('issue')}): fix it, restore it, or renew the entry with a reason")
        elif dates["review"] and dates["review"] < today:
            report.warning(f"Quarantine review date {entry['review']} has passed for {test} "
                           f"(owner {entry.get('owner')})")


def collect_findings(base, head, changes):
    return (test_findings(base, head) + assertion_findings(base, head, changes) + file_findings(changes)
            + ci_line_findings(base, head, changes) + corpus_findings(base, head) + fuzz_findings(base, head)
            + registry_findings(base, head) + proof_findings(base, head))


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--base", required=True, help="Base revision (the PR base or HEAD^)")
    parser.add_argument("--head", default="HEAD")
    args = parser.parse_args(argv)
    base, head = common.Tree(args.base), common.Tree(args.head)
    report = common.Report("Coverage regression")
    findings, unresolved, quarantined = [], [], []
    try:
        findings = collect_findings(base, head, common.changed_files(args.base, args.head))
        unresolved = check_ledger(base, head, findings, report)
        quarantined = common.section(head.read_json(LEDGER, {}) or {}, "quarantine", list, common.Report(""), LEDGER)
    except (common.MalformedJson, AttributeError, TypeError, KeyError) as error:
        # A malformed ledger, corpus or registry must fail with its cause, not a bare traceback.
        traceback.print_exc(file=sys.stderr)
        report.error(f"Could not evaluate coverage because a JSON file is malformed: {error}")
    finally:
        base.close()
        head.close()
    if findings:
        rows = ["| Kind | Subject | Detail | Disposition |", "| --- | --- | --- | --- |"]
        rows += [f"| {item.kind} | `{item.subject}` | {item.detail} | {'missing' if item in unresolved else 'recorded'} |"
                 for item in findings]
        report.section("\n".join(rows))
    else:
        report.section("No coverage-relevant changes found.")
    if quarantined:
        report.section(f"{len(quarantined)} quarantined tests remain visible coverage gaps; see `{LEDGER}`.")
    return report.finish()


if __name__ == "__main__":
    sys.exit(main())
