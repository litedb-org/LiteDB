"""Summarize a PR's test evidence for GitHub: a check named with the counts, plus labels.

`summarize` runs in the Regression proof workflow (read-only token). It counts
test methods added and removed against the base and tallies the regression
proofs whose reports show the known-bad state failing and the candidate passing.
It writes the badge text (used as the name of an informational check, so the
counts show in the PR's checks list) and pr-evidence.json for the labeler.

`labels` runs in the trusted workflow_run workflow. It treats pr-evidence.json as
untrusted data: every field is type-checked, and the PR must still have the head
commit the evidence was computed for. What the trusted side can confirm itself
comes from the API and the event (the PR's labels, its changed files, the run's
conclusion); a PR that changes the proving harness never earns "proven". The
label stays advisory: the repro is PR-authored and must be reviewed.
"""
import argparse
import json
import os
import re
import sys
from pathlib import Path

import regression_proof
import safety_common as common

PROVEN = "regression: proven"
NEEDS_PROOF = "regression: needs proof"
# The evidence comes from PR-controlled code; a PR that changes any of this code
# could forge it, so its evidence never earns the "proven" label. The scripts are
# the full import closure of pr_evidence.py; a new import must edit one of them.
HARNESS_FILES = {
    ".github/workflows/regression-proof.yml", ".github/workflows/pr-evidence-labels.yml",
    ".github/scripts/regression_proof.py", ".github/scripts/pr_evidence.py", ".github/scripts/repro_scaffold.py",
    ".github/scripts/proof_provenance.py", ".github/scripts/safety_common.py",
}
HARNESS_DIRS = ("LiteDB.ReproRunner/LiteDB.ReproRunner.Cli/", "LiteDB.ReproRunner/LiteDB.ReproRunner.Shared/")


def test_counts(base, head):
    before, after = common.collect_tests(common.Tree(base)), common.collect_tests(common.Tree(head))
    return len(set(after) - set(before)), len(set(before) - set(after))


def tally(reports, matrix):
    """Number of selected proofs whose report proves the fix."""
    proven = 0
    for item in matrix:
        path = Path(reports) / f"regression-proof-{item['repro']}" / "proof.json"
        report = common.Report("")
        if path.is_file():
            try:
                regression_proof.verify(path, item["repro"], report, item.get("version"))
            except (ValueError, KeyError, TypeError) as error:
                report.errors.append(str(error))
            proven += not report.errors
    return proven


def badge(added, removed, proven, total, bug, new_proofs):
    tests = f"+{added} tests" + (f" (−{removed})" if removed else "")
    if total:
        proofs = f"{proven}/{total} proven to fail before"
    elif bug and new_proofs < regression_proof.MIN_BUG_PROOFS:
        proofs = "bug fix without regression proof"
    else:
        proofs = "no regression proof"
    return f"Evidence: {tests} · {proofs}"


def summarize(args):
    added, removed = test_counts(args.base, args.head)
    matrix = json.loads(args.matrix or '{"include": []}').get("include", [])
    proven = tally(args.reports, matrix) if matrix else 0
    entries = regression_proof.load(common.Tree(args.head), common.Report(""))
    new_count = len(regression_proof.new_proofs(args.base, entries))
    bug = args.bug or regression_proof.is_bug_fix(regression_proof.parse_labels(args.labels))
    text = badge(added, removed, proven, len(matrix), bug, new_count)
    evidence = {"pr": args.pr, "headSha": args.head_sha, "testsAdded": added, "testsRemoved": removed,
                "proofsTotal": len(matrix), "proofsProven": proven, "newProofs": new_count, "bug": bug}
    Path(args.output).write_text(json.dumps(evidence), encoding="utf-8")
    target = os.environ.get("GITHUB_OUTPUT")
    if target:
        with open(target, "a", encoding="utf-8") as handle:
            handle.write(f"badge={text}\n")
    print(text)
    return 0


def parse_evidence(text):
    """Strictly typed fields from an untrusted pr-evidence.json, or ValueError."""
    data = json.loads(text)
    if not isinstance(data, dict):
        raise ValueError("evidence must be an object")
    ints = ("pr", "testsAdded", "testsRemoved", "proofsTotal", "proofsProven", "newProofs")
    if any(not isinstance(data.get(key), int) or isinstance(data.get(key), bool) or data[key] < 0 for key in ints):
        raise ValueError("counts must be non-negative integers")
    if not isinstance(data.get("bug"), bool) or not re.fullmatch(r"[0-9a-f]{40}", str(data.get("headSha"))):
        raise ValueError("bug must be a boolean and headSha a commit id")
    if data["proofsProven"] > data["proofsTotal"]:
        raise ValueError("more proofs proven than selected")
    return {key: data[key] for key in ints + ("bug", "headSha")}


def harness_changed(paths):
    """True when the PR changes code that produces or judges the evidence."""
    return any(path in HARNESS_FILES or path.startswith(HARNESS_DIRS) for path in paths)


def wanted_labels(evidence, bug, run_succeeded, harness):
    """Labels from PR-produced counts, trusted only as far as the trusted side can confirm:
    the run succeeded, the PR left the harness alone, and bug comes from the PR's labels."""
    proven = (run_succeeded and not harness and evidence["proofsTotal"] > 0
              and evidence["proofsProven"] == evidence["proofsTotal"])
    missing = bug and (not proven or evidence["newProofs"] < regression_proof.MIN_BUG_PROOFS)
    return {PROVEN: proven, NEEDS_PROOF: missing}


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    commands = parser.add_subparsers(dest="command", required=True)
    run = commands.add_parser("summarize")
    run.add_argument("--base", required=True)
    run.add_argument("--head", default="HEAD")
    run.add_argument("--head-sha", required=True, help="The PR head commit (not the merge ref)")
    run.add_argument("--pr", required=True, type=int)
    run.add_argument("--matrix", default="", help="The select job's matrix JSON")
    run.add_argument("--reports", default=".", help="Directory holding regression-proof-* artifacts")
    run.add_argument("--bug", action="store_true")
    run.add_argument("--labels", help="The PR's labels as a JSON array; a bug-fix label implies --bug")
    run.add_argument("--output", default="pr-evidence.json")
    labels = commands.add_parser("labels", help="Print the label changes for a pr-evidence.json as JSON")
    labels.add_argument("--evidence", required=True)
    labels.add_argument("--pr-labels", required=True, help="File with the PR's current labels, one per line (API)")
    labels.add_argument("--changed-files", required=True, help="File with the PR's changed paths, one per line (API)")
    labels.add_argument("--run-conclusion", required=True, help="The Regression proof run's conclusion (event)")
    args = parser.parse_args(argv)
    if args.command == "summarize":
        return summarize(args)
    evidence = parse_evidence(Path(args.evidence).read_text(encoding="utf-8"))
    bug = regression_proof.is_bug_fix(Path(args.pr_labels).read_text(encoding="utf-8").splitlines())
    harness = harness_changed(Path(args.changed_files).read_text(encoding="utf-8").splitlines())
    result = wanted_labels(evidence, bug, args.run_conclusion == "success", harness)
    print(json.dumps({"pr": evidence["pr"], "headSha": evidence["headSha"], "labels": result,
                      "harnessChanged": harness}))
    return 0


if __name__ == "__main__":
    sys.exit(main())
