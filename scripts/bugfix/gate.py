#!/usr/bin/env python3
"""Deterministic bugfix gates. Run this file from a trusted controller checkout."""

import argparse
import json
from pathlib import Path
import subprocess
import sys

from failure_normalization import load_failure_normalization
from policy import (compare_ledger, load_baseline_policy, load_issue,
                    load_test_inventory, make_ledger, require_sha, verify_focused,
                    verify_target)
from protect import verify_changes, verify_frozen_tests
from trx import GateError, read_trx


def parser():
    root = argparse.ArgumentParser(description=__doc__)
    commands = root.add_subparsers(dest="command", required=True)
    for name in ("baseline", "focused", "snapshot", "compare", "protect", "verify-tests"):
        command = commands.add_parser(name)
        command.add_argument("--manifest", default=str(Path(__file__).with_name("issues.json")))
        command.add_argument("--issue", required=True, type=int)
        command.add_argument("--base-sha", required=True)
        command.add_argument("--output", required=True)
        if name in ("protect", "verify-tests"):
            command.add_argument("--repository", required=True)
            if name == "protect":
                command.add_argument("--candidate-sha", required=True)
            continue
        command.add_argument("--test-definition-sha", required=True)
        command.add_argument("--environment", required=True)
        if name in ("baseline", "focused", "snapshot"):
            command.add_argument("--baseline-trx", required=True)
            command.add_argument("--baseline-exit-code", required=True, type=int)
        if name in ("focused", "compare"):
            command.add_argument("--candidate-sha", required=True)
            command.add_argument("--candidate-trx", required=True)
            command.add_argument("--candidate-exit-code", required=True, type=int)
        if name == "snapshot":
            command.add_argument("--allowed-failure-classes", required=True)
        if name in ("snapshot", "compare"):
            command.add_argument("--test-inventory", required=True)
            command.add_argument("--failure-normalization", required=True)
        if name == "compare":
            command.add_argument("--ledger", required=True)
    return root


def evaluate(args):
    issue, manifest_hash = load_issue(args.manifest, args.issue)
    provenance = {"base_sha": require_sha(args.base_sha), "issue": args.issue,
                  "manifest_sha256": manifest_hash}
    if hasattr(args, "candidate_sha"):
        provenance["candidate_sha"] = require_sha(args.candidate_sha)
    if args.command == "protect":
        paths = verify_changes(args.repository, args.base_sha, args.candidate_sha, issue)
        return {"accepted": True, "outcome": "scope_verified", "changed_paths": paths,
                "provenance": provenance}
    if args.command == "verify-tests":
        paths = verify_frozen_tests(args.repository, args.base_sha, issue)
        return {"accepted": True, "outcome": "tests_unchanged", "protected_paths": paths,
                "provenance": provenance}
    provenance.update(test_definition_sha=require_sha(args.test_definition_sha),
                      environment=args.environment)
    if args.environment not in issue["environments"]:
        raise GateError("Environment is not approved by the issue contract")
    baseline = None
    if hasattr(args, "baseline_trx"):
        baseline = read_trx(args.baseline_trx, args.baseline_exit_code)
    if args.command in ("baseline", "focused"):
        verify_focused(baseline, issue, baseline=True)
        result = {"accepted": True, "outcome": "bug_present",
                  "baseline_trx_sha256": baseline.sha256, "test_count": len(baseline.tests)}
        if args.command == "focused":
            candidate = read_trx(args.candidate_trx, args.candidate_exit_code)
            verify_focused(candidate, issue, baseline=False)
            result.update(outcome="behavior_correct", candidate_trx_sha256=candidate.sha256)
    elif args.command == "snapshot":
        verify_target(baseline, issue, baseline=True)
        allowed_classes, allowed_skips, intermittent_classes = load_baseline_policy(
            args.allowed_failure_classes)
        normalization, normalization_hash = load_failure_normalization(args.failure_normalization)
        provenance["failure_normalization_sha256"] = normalization_hash
        result = make_ledger(baseline, provenance, allowed_classes,
                             load_test_inventory(args.test_inventory), allowed_skips,
                             normalization, intermittent_classes)
        result.update(accepted=True, outcome="baseline_recorded")
    else:
        candidate = read_trx(args.candidate_trx, args.candidate_exit_code)
        ledger = json.loads(Path(args.ledger).read_text(encoding="utf-8"))
        normalization, normalization_hash = load_failure_normalization(args.failure_normalization)
        provenance["failure_normalization_sha256"] = normalization_hash
        result = compare_ledger(ledger, candidate, issue, provenance,
                                load_test_inventory(args.test_inventory), normalization)
        result["outcome"] = "behavior_correct" if result["accepted"] else "inconclusive"
    result["provenance"] = provenance
    return result


def main(argv=None):
    args = parser().parse_args(argv)
    try:
        report = evaluate(args)
    except (GateError, OSError, ValueError, KeyError, TypeError, subprocess.CalledProcessError) as error:
        report = {"accepted": False, "outcome": "harness_error", "errors": [str(error)]}
    report["schema_version"] = 1
    path = Path(args.output)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    # Windows consoles are not reliably UTF-8 even when the evidence contains
    # valid Unicode display names. The report file above remains human-readable.
    print(json.dumps(report, ensure_ascii=True))
    return 0 if report["accepted"] else 1


if __name__ == "__main__":
    sys.exit(main())
