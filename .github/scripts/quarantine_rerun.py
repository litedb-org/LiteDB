"""Weekly re-run of quarantined and known-finding tests (.github/workflows/quarantine-rerun.yml).

A quarantined test (coverage-ledger.json `quarantine`) runs on no CI leg, and a known-finding
test (a method named `Known_finding_*`) pins a defect's current behavior. Neither may sit
unwatched: this script plans one run of all of them and reports each entry's result.

  plan    list the entries, write the vstest filter, and with --unskip remove the `Skip = ...`
          argument of each quarantined test's [Fact]/[Theory] in the working tree (for the
          throwaway CI checkout only: a quarantine is re-run, not silently restored)
  report  read the TRX results and print one row per entry. Needs attention (exit 1): a
          quarantined test that now passes (restore it), a known-finding test that now fails (the
          finding changed or was fixed: update its registration), an entry with no result, a
          passed review date or an expired quarantine.
"""
import argparse
import json
import re
import sys
from datetime import date
from pathlib import Path

import check_test_evidence as evidence
import safety_common as common

LEDGER = f"{common.SAFETY_DIR}/coverage-ledger.json"
KNOWN_FINDING = re.compile(r"Known_finding_\w+\Z")
ATTRIBUTE = re.compile(r"\[\s*(?:\w+\.)*\w*(?:Fact|Theory)(?:Attribute)?\s*\(")


def entries(tree):
    tests = common.collect_tests(tree)
    ledger = tree.read_json(LEDGER, {}) or {}
    planned = []
    for item in ledger.get("quarantine", []):
        found = tests.get(item.get("test"))
        planned.append({"test": item.get("test"), "kind": "quarantine", "path": found[0] if found else None,
                        "skipped": bool(found and found[1].skipped_variants), "owner": item.get("owner"),
                        "issue": item.get("issue"), "review": item.get("review"), "expires": item.get("expires")})
    for fqn, (path, method) in sorted(tests.items()):
        if KNOWN_FINDING.match(method.name):
            planned.append({"test": fqn, "kind": "known-finding", "path": path,
                            "skipped": bool(method.skipped_variants)})
    return planned


def vstest_filter(planned):
    return "|".join(f"FullyQualifiedName={item['test']}" for item in planned if item["path"])


def unskip(text, method_name, start):
    """Drop the Skip argument of every Fact/Theory attribute between `start` and the method name."""
    code = common.blank_code(text)
    name = re.compile(r"\b" + re.escape(method_name) + r"\s*[(<]").search(code, start)
    end = name.start() if name else len(code)
    for match in reversed(list(ATTRIBUTE.finditer(code, start, end))):
        open_paren = match.end() - 1
        close = common.matching(code, open_paren, "(", ")") - 1
        arguments, cursor, depth = [], open_paren + 1, 0
        for index in range(open_paren + 1, close + 1):
            char = code[index] if index < close else ","
            depth += char in "([{"
            depth -= char in ")]}"
            if char == "," and depth == 0:
                arguments.append(text[cursor:index])
                cursor = index + 1
        kept = [argument.strip() for argument in arguments if not re.match(r"\s*Skip\s*=", argument)]
        replacement = f"({', '.join(kept)})" if kept else ""
        text = text[:open_paren] + replacement + text[close + 1:]
        code = common.blank_code(text)
    return text


def plan(args):
    tree = common.Tree(common.WORKTREE)
    planned = entries(tree)
    if args.unskip:
        for item in planned:
            if item["kind"] == "quarantine" and item["skipped"]:
                file = Path(common.repo_root()) / item["path"]
                raw = file.read_bytes()
                bom = raw.startswith(b"\xef\xbb\xbf")
                text = raw.decode("utf-8-sig")  # bytes keep the file's BOM and line endings
                method = common.parse_tests(text)[item["test"]]  # parse again: an earlier edit moved offsets
                file.write_bytes((b"\xef\xbb\xbf" if bom else b"") + unskip(text, method.name, method.start).encode())
                print(f"Unskipped {item['test']} in {item['path']} (this checkout only)")
    projects = sorted({item["path"].split("/", 1)[0] for item in planned if item["path"]})
    Path(args.output).write_text(json.dumps({"entries": planned, "projects": projects,
                                             "filter": vstest_filter(planned)}, indent=2) + "\n", encoding="utf-8")
    print(f"{len(planned)} entries in {', '.join(projects) or 'no project'}")
    return 0


def outcomes(directory):
    results = {}
    for path in sorted(Path(directory).rglob("*.trx")):
        for fqn, outcome in evidence.read_trx(path)[0]:
            results.setdefault(fqn, set()).add(outcome)
    return results


def judge(item, results, today):
    seen = results.get(item["test"], set())
    failed = bool(seen & {"Failed", "Fail", "Error", "Timeout"})
    outcome = "no result" if not seen or seen <= {"NotExecuted", "Skipped"} else "failed" if failed else "passed"
    notes = []
    if outcome == "no result":
        notes.append("no result: the test did not run (renamed, filtered out or still skipped)")
    elif item["kind"] == "quarantine" and outcome == "passed":
        notes.append("passes: restore it to a CI leg and remove its quarantine entry")
    elif item["kind"] == "known-finding" and outcome == "failed":
        notes.append("no longer reproduces as pinned: the finding changed or was fixed; update its registration")
    for name, label in (("expires", "quarantine expired"), ("review", "review date passed")):
        try:
            if item.get(name) and date.fromisoformat(item[name]) < today:
                notes.append(f"{label} ({item[name]}, owner {item.get('owner')}, {item.get('issue')})")
                break
        except ValueError:
            notes.append(f"{name} is not an ISO date")
    return outcome, notes


def report(args):
    planned = json.loads(Path(args.plan).read_text(encoding="utf-8"))["entries"]
    results = outcomes(args.results)
    today = date.fromisoformat(args.today) if args.today else date.today()
    out = common.Report("Quarantine and known-finding re-run")
    rows = ["| Entry | Kind | Result | Needs attention |", "| --- | --- | --- | --- |"]
    judged = []
    for item in planned:
        outcome, notes = judge(item, results, today)
        judged.append({**item, "outcome": outcome, "attention": notes})
        rows.append(f"| `{item['test']}` | {item['kind']} | {outcome} | {'; '.join(notes) or '-'} |")
        for note in notes:
            out.error(f"{item['test']}: {note}")
    out.section("\n".join(rows))
    if args.output:
        Path(args.output).write_text(json.dumps(judged, indent=2) + "\n", encoding="utf-8")
    return out.finish()


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    commands = parser.add_subparsers(dest="command", required=True)
    planning = commands.add_parser("plan")
    planning.add_argument("--output", required=True, help="Plan JSON (entries, projects, vstest filter)")
    planning.add_argument("--unskip", action="store_true", help="Remove Skip from quarantined tests in this checkout")
    reporting = commands.add_parser("report")
    reporting.add_argument("--plan", required=True)
    reporting.add_argument("--results", required=True, help="Directory holding the run's TRX files")
    reporting.add_argument("--output", help="Write the judged entries as JSON")
    reporting.add_argument("--today", help="ISO date to judge review and expiry dates against (tests)")
    args = parser.parse_args(argv)
    return plan(args) if args.command == "plan" else report(args)


if __name__ == "__main__":
    sys.exit(main())
