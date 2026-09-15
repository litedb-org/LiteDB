"""Run an explicit approved issue list serially through pinned per-fix integration."""

import argparse
import json
from pathlib import Path
import re
import subprocess
import sys

from orchestrate import TEST_SOURCE
from patching import git, worktree
from queue_support import inspect_queue_issue, pinned_manifest
from state import NAME, SHA, Rejected, require


def invoke(script, arguments, repository):
    result = subprocess.run([sys.executable, str(script), *arguments], cwd=repository, check=False)
    require(result.returncode == 0, f"Queue stopped: {script.name} exited {result.returncode}; inspect its existing campaign")


def campaign_arguments(args, decision):
    return ["--repo", args.repo, "--campaign", decision["campaign"], "--issue", str(decision["issue"]),
            "--integration-base", decision["base_sha"], "--workflow-sha", args.workflow_sha,
            "--workflow-ref", args.workflow_ref, "--test-source-sha", args.test_source_sha,
            "--repository", str(args.repository), "--max-runs", str(args.max_runs),
            "--timeout-minutes", str(args.timeout_minutes)]


def run_issue(args, issue, contracts, control):
    decision = inspect_queue_issue(args, issue, contracts)
    print(json.dumps({"queue_issue": decision}, ensure_ascii=False), flush=True)
    if decision["action"] == "skip_accepted":
        return decision
    if decision["phase"] != "ready":
        invoke(control / ".github/bugfix/orchestrate.py", campaign_arguments(args, decision), args.repository)
    ready = inspect_queue_issue(args, issue, contracts)
    if ready["action"] == "skip_accepted":
        return ready
    require(ready["phase"] == "ready" and ready["candidate_sha"], "Campaign did not reach validated ready state")
    require(ready["base_sha"] == decision["base_sha"], "Campaign base changed during queue execution")
    integration = ["--repo", args.repo, "--campaign", ready["campaign"], "--candidate-sha", ready["candidate_sha"],
                   "--repository", str(args.repository)]
    invoke(control / ".github/bugfix/integrate.py", integration, args.repository)
    # The integrator owns evidence verification, expected-base lease, lock and permanent ledger.
    apply = [*integration, "--apply"] + (["--resume"] if ready["resume_integration"] else [])
    invoke(control / ".github/bugfix/integrate.py", apply, args.repository)
    accepted = inspect_queue_issue(args, issue, contracts)
    require(accepted["action"] == "skip_accepted" and accepted["candidate_sha"] == ready["candidate_sha"],
            "Integration did not durably accept the exact tested candidate")
    return {**accepted, "action": "integrated"}


def execute(args):
    manifest = pinned_manifest(args.repo, args.workflow_sha, args.workflow_ref)
    contracts = manifest["issues"]
    for issue in args.issues:
        contract = contracts.get(str(issue))
        require(contract and contract.get("inventory_issue") == issue and contract.get("frozen_test_revision") == TEST_SOURCE,
                f"Issue {issue} has no approved contract at the pinned runtime")
        require(contract.get("regressions") and contract.get("controls") and contract.get("allowed_production_paths"),
                f"Issue {issue} has an incomplete approved contract")
    if args.dry_run:
        decisions = [inspect_queue_issue(args, issue, contracts) for issue in args.issues]
        pending = False
        for decision in decisions:
            if pending and decision["action"] != "skip_accepted":
                decision["base_sha"] = "resolved after preceding issue integration"
            pending = pending or decision["action"] != "skip_accepted"
        return {"dry_run": True, "workflow_sha": args.workflow_sha, "workflow_ref": args.workflow_ref,
                "issues": decisions, "full_ci": False, "mutations": False}
    git(args.repository, "fetch", "--quiet", f"https://github.com/{args.repo}.git", args.workflow_sha)
    results = []
    with worktree(args.repository, args.workflow_sha) as control:
        for issue in args.issues:
            # Pin is checked again between issues, even when a previous campaign completed earlier.
            pinned_manifest(args.repo, args.workflow_sha, args.workflow_ref)
            results.append(run_issue(args, issue, contracts, control))
    return {"phase": "complete", "issues": results, "full_ci": False}


def arguments(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("repo", "workflow-sha", "workflow-ref", "campaign-prefix"):
        parser.add_argument("--" + name, required=True)
    parser.add_argument("--issues", required=True, nargs="+", type=int, help="Explicit ordered approved issue IDs")
    parser.add_argument("--repository", type=Path, default=Path.cwd())
    parser.add_argument("--max-runs", type=int, default=40)
    parser.add_argument("--timeout-minutes", type=int, default=180)
    parser.add_argument("--dry-run", action="store_true", help="Read-only snapshot preview; no fetch, checkout, writes or dispatch")
    args = parser.parse_args(argv)
    require(re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", args.repo), "Invalid repository")
    require(SHA.fullmatch(args.workflow_sha), "Full immutable workflow SHA required")
    require(re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_./-]*", args.workflow_ref)
            and ".." not in args.workflow_ref and "//" not in args.workflow_ref, "Invalid workflow branch")
    require(1 <= len(args.issues) <= 20 and len(set(args.issues)) == len(args.issues) and min(args.issues) > 0,
            "Supply one to twenty distinct positive issue IDs")
    require(all(NAME.fullmatch(f"{args.campaign_prefix}-{issue}") for issue in args.issues), "Invalid deterministic campaign prefix")
    require(1 <= args.max_runs <= 40 and 1 <= args.timeout_minutes <= 180, "Invalid per-campaign run or timeout bound")
    args.repository = args.repository.resolve()
    args.test_source_sha = TEST_SOURCE
    return args


def main():
    print(json.dumps(execute(arguments()), indent=2, ensure_ascii=False))


if __name__ == "__main__":
    try:
        main()
    except (Rejected, ValueError, OSError, KeyError) as error:
        print(f"bugfix-queue: {error}", file=sys.stderr)
        sys.exit(1)
