"""Classify a change as `critical` from its diff (docs/rules/implement-safely.md#critical-changes).

A change is critical when it touches lifetime, ownership, locks, waits, teardown,
durability, exception contracts or public API surface, or the safety machinery
that judges such changes. The rules, each reported with the files that hit it:

- paths: LiteDB/Engine/Services/, LiteDB/Client/Shared/, LiteDB/Client/Transactions/,
  LiteDB/Client/Direct/;
- changed code lines (comments ignored) in LiteDB/ naming a Dispose/Close/Release/
  finally path, a WAL/checkpoint/flush call, or declaring a public member;
- exception-contract widening heuristics: an added `throw`, a removed `catch`;
- public API members on the ILite* interfaces or new public types
  (check_reachability.api_changes, the same rule that demands `api:` markers);
- the fuzz obligation map (.github/safety/fuzz-obligations.json): every obligation of
  a kind in MAP_KINDS or with an id in MAP_IDS that matches the change;
- safety machinery: the `safety-machinery` contract's paths in contracts.json plus
  SAFETY_MACHINERY below (oracles, registries, scripts, workflows, corpus
  expectations, proof entries, test settings, the PR template).

The label never gates a merge by itself. It selects heavier CI tiers (every fuzz
target in the PR-selected job, the mutation pilot) and the critical sections of the
PR description (check_pr_section.py). The rules and maps are read from --config-rev
(default: the working tree), so the trusted labeler judges a PR with its base's rules.
"""
import argparse
import json
import os
import re
import sys

import check_reachability
import safety_common as common

OBLIGATIONS = f"{common.SAFETY_DIR}/fuzz-obligations.json"
CONTRACTS = f"{common.SAFETY_DIR}/contracts.json"
LABEL = "critical"
SOURCE = ["LiteDB/**/*.cs"]
COMMENT = re.compile(r"\s*(?://|/\*|\*)")
MAP_KINDS = {"lock-wait", "teardown", "public-api", "fault-point", "transaction", "shared", "safety-machinery",
             "fuzz-harness"}
MAP_IDS = {"disk-wal", "mvcc-checkpoint"}
SAFETY_MACHINERY = [
    ".github/safety/**", ".github/scripts/**", ".github/workflows/**", ".github/pull_request_template.md",
    "LiteDB/Utils/Reachability.cs", "LiteDB/Utils/WaitGraph*.cs", "LiteDB/Utils/Teardown*.cs",
    "LiteDB/Client/Shared/SharedOwnershipEvents.cs", "LiteDB.Tests/Safety/**", "LiteDB.Tests/Utils/WaitGraph*.cs",
    "LiteDB.Tests/Engine/ConcurrencyExplorer/**", "LiteDB.Tests/Concurrency/**", "LiteDB.Tests.Concurrency/**",
    "LiteDB.Fuzz/Fuzz*.cs", "LiteDB.Fuzz/Program.cs", "LiteDB.Fuzz/Corpus/**", "LiteDB.Fuzz/SelfTests/**",
    "LiteDB.Fuzz/Targets/**", "LiteDB.Fuzz.Tests/**", "tools/net-proofs/**", "tests.runsettings",
    "tests.ci.runsettings", "scripts/run-ci-tests.ps1", "LiteDB.Tests/stryker-config.json", ".config/dotnet-tools.json",
]


class Rule:
    def __init__(self, id, reason, paths, both=(), added=(), removed=()):
        self.id, self.reason = id, reason
        self.globs = [common.glob_regex(glob) for glob in paths]
        self.both = [re.compile(pattern) for pattern in both]
        self.added = [re.compile(pattern) for pattern in added]
        self.removed = [re.compile(pattern) for pattern in removed]

    def match(self, path, lines):
        """None when the rule does not apply, else a short detail (the first matching line)."""
        if not any(glob.match(path) for glob in self.globs):
            return None
        if not (self.both or self.added or self.removed):
            return "path"
        for sign, text in lines:
            if COMMENT.match(text):
                continue
            patterns = self.both + (self.added if sign == "+" else self.removed)
            if any(pattern.search(text) for pattern in patterns):
                return f"{sign} {text.strip()[:100]}"
        return None


RULES = [
    Rule("engine-services", "LiteDB/Engine/Services holds the locks, gates, lifetimes and monitors",
         ["LiteDB/Engine/Services/**"]),
    Rule("shared-client", "Shared-mode connections, ownership and the writer mutex", ["LiteDB/Client/Shared/**"]),
    Rule("transactions-client", "Transaction handles and their lifetimes", ["LiteDB/Client/Transactions/**"]),
    Rule("direct-client", "Direct-mode engine pooling and leases", ["LiteDB/Client/Direct/**"]),
    Rule("teardown-path", "A Dispose/Close/Release/finally path changed", SOURCE,
         both=[r"\bDispose(?:Async)?\b", r"\bClose\w*\s*\(", r"\bRelease\w*\s*\(", r"\bfinally\b", r"~\w+\s*\(\s*\)"]),
    Rule("wal-checkpoint-flush", "A WAL, checkpoint or flush path changed", SOURCE,
         both=[r"(?:\b_?[Ww]al|(?<=[a-z])Wal)(?=[A-Z_\d]|\b)|\bWAL\b", r"\b\w*[Cc]heckpoint\w*\b",
               r"\bFlush\w*\s*\(", r"\bSetLength\s*\("]),
    Rule("exception-contract", "A method may start throwing, throw a new type or stop catching", SOURCE,
         added=[r"\bthrow\b"], removed=[r"\bcatch\b"]),
    Rule("public-member", "A public declaration was added, removed or changed", SOURCE,
         both=[r"^\s*(?:\[[^\]]*\]\s*)*public\s"]),
]


def map_obligations(tree, report):
    """Critical-kind obligations of the fuzz obligation map, compiled; invalid entries are skipped."""
    try:
        data = tree.read_json(OBLIGATIONS) or {}
    except common.MalformedJson as error:
        report.warning(f"{error}; classifying with the built-in rules only")
        return []
    if not data:
        report.warning(f"{OBLIGATIONS} is missing; classifying with the built-in rules only")
    rules = []
    for item in data.get("obligations", []) if isinstance(data, dict) else []:
        if not isinstance(item, dict) or (item.get("kind") not in MAP_KINDS and item.get("id") not in MAP_IDS):
            continue
        try:
            rules.append(Rule(f"obligation:{item['id']}", str(item.get("reason", ""))[:120], item["paths"],
                              both=item.get("patterns", [])))
        except (KeyError, TypeError, re.error) as error:
            report.warning(f"{OBLIGATIONS}: obligation {item.get('id')!r} skipped ({error})")
    return rules


def machinery_rule(tree, report):
    paths = list(SAFETY_MACHINERY)
    try:
        index = tree.read_json(CONTRACTS) or {}
    except common.MalformedJson as error:
        report.warning(str(error))
        index = {}
    for contract in index.get("contracts", []) if isinstance(index, dict) else []:
        if isinstance(contract, dict) and contract.get("id") == "safety-machinery":
            paths += [glob for glob in contract.get("paths", []) if isinstance(glob, str) and glob not in paths]
    return Rule("safety-machinery", "Safety machinery: oracles, registries, scripts, workflows, corpus "
                "expectations, proof entries or test settings", paths)


def changed_lines(base, head):
    """({path: status}, {path: [(sign, text)]}) for the diff base..head."""
    changes = common.changed_files(base, head)
    lines = {path: [] for path in changes}
    for sign, path, _, text in common.diff_lines(base, head, []):
        lines.setdefault(path, []).append((sign, text))
    return changes, lines


def classify(changes, lines, rules, api=()):
    """Return [{rule, reason, file, detail}] for every rule a changed file hits."""
    reasons = [{"rule": "public-api", "reason": "Public API surface on an ILite* interface or a new public type",
                "file": where.split(" in ")[-1], "detail": marker} for marker, where in api]
    for path in sorted(changes):
        for rule in rules:
            detail = rule.match(path, lines.get(path, []))
            if detail is not None:
                reasons.append({"rule": rule.id, "reason": rule.reason, "file": path, "detail": detail})
    return reasons


def resolve_base(base, head):
    try:
        return common.git("merge-base", base, head).strip()
    except Exception:  # unrelated histories or a shallow clone: diff against the given base
        return common.git("rev-parse", base).strip()


def evaluate(base, head, config_rev=common.WORKTREE, report=None):
    """The classification dict for base..head (base is reduced to the merge-base)."""
    report = report or common.Report("Critical change")
    head = common.git("rev-parse", head).strip()
    base = resolve_base(base, head)
    config = common.Tree(config_rev)
    rules = RULES + [machinery_rule(config, report)] + map_obligations(config, report)
    changes, lines = changed_lines(base, head)
    api = list(check_reachability.api_changes(common.Tree(base), common.Tree(head), set(changes)))
    reasons = classify(changes, lines, rules, api)
    return {"schemaVersion": 1, "base": base, "head": head, "critical": bool(reasons), "reasons": reasons}


def summary(result):
    if not result["critical"]:
        return "Not critical: no rule matched the diff."
    rows = ["| Rule | Files (first 5) | Example |", "| --- | --- | --- |"]
    grouped = {}
    for item in result["reasons"]:
        grouped.setdefault(item["rule"], []).append(item)
    for rule, items in sorted(grouped.items()):
        files = ", ".join(f"`{_safe(item['file'])}`" for item in items[:5]) + (" ..." if len(items) > 5 else "")
        rows.append(f"| {rule} | {files} | `{_safe(items[0]['detail'])[:80]}` |")
    return "Critical: the diff matches the rules below.\n\n" + "\n".join(rows)


def _safe(text):
    """PR-controlled text inside inline code: no backticks, no pipes, no mentions."""
    return str(text).replace("`", "'").replace("|", "/").replace("@", "@​")


def comment(result, actor):
    return (f"The `{LABEL}` label was removed{f' by @{_safe(actor)}' if actor else ''}, but the diff is critical, "
            "so it was applied again. It does not block merging by itself: it selects the heavier CI tiers "
            "(every fuzz target, mutation on the diff) and the critical sections of the PR description. "
            "See docs/rules/implement-safely.md#critical-changes.\n\n" + summary(result) + "\n")


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--base", default="origin/dev", help="Diff base; its merge-base with --head is used")
    parser.add_argument("--head", default="HEAD")
    parser.add_argument("--config-rev", default=common.WORKTREE,
                        help="Revision whose obligation map and contracts are used (default: the working tree)")
    parser.add_argument("--output", help="Write the classification JSON here")
    parser.add_argument("--github-output", action="store_true", help="Append critical=true|false to $GITHUB_OUTPUT")
    parser.add_argument("--comment", help="Write the re-application comment (markdown) here")
    parser.add_argument("--actor", help="Who removed the label (for --comment)")
    args = parser.parse_args(argv)
    report = common.Report("Critical change")
    result = evaluate(args.base, args.head, args.config_rev, report)
    report.section(summary(result))
    if args.output:
        with open(args.output, "w", encoding="utf-8", newline="\n") as handle:
            json.dump(result, handle, indent=2)
            handle.write("\n")
    if args.comment:
        with open(args.comment, "w", encoding="utf-8", newline="\n") as handle:
            handle.write(comment(result, args.actor))
    value = f"critical={'true' if result['critical'] else 'false'}"
    target = os.environ.get("GITHUB_OUTPUT") if args.github_output else None
    if target:
        with open(target, "a", encoding="utf-8") as handle:
            handle.write(value + "\n")
    print(value)
    return report.finish()


if __name__ == "__main__":
    sys.exit(main())
