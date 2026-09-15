"""Run or resume one bounded correctness-issue canary, ending before integration."""

import argparse
import json
from pathlib import Path
import re
import sys

from campaign import Campaign
from patching import git, worktree
from state import Rejected, new_state, require

TEST_SOURCE = "dd937719f7eee53c512f50ac604cab639bf42a4c"


def arguments(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", required=True)
    parser.add_argument("--campaign", required=True)
    parser.add_argument("--issue", type=int, required=True)
    parser.add_argument("--integration-base", required=True)
    parser.add_argument("--workflow-sha", required=True)
    parser.add_argument("--workflow-ref", required=True, help="Branch whose HEAD must remain the pinned workflow SHA")
    parser.add_argument("--test-source-sha", default=TEST_SOURCE)
    parser.add_argument("--repository", type=Path, default=Path.cwd())
    parser.add_argument("--max-runs", type=int, default=40)
    parser.add_argument("--timeout-minutes", type=int, default=180)
    parser.add_argument("--dry-run", action="store_true", help="Print the bounded operation plan without any remote actions")
    args = parser.parse_args(argv)
    require(re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", args.repo), "Invalid GitHub repository")
    require(re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_./-]*", args.workflow_ref)
            and ".." not in args.workflow_ref and "//" not in args.workflow_ref, "Invalid workflow branch")
    new_state(args.campaign, args.issue, args.integration_base, args.test_source_sha, args.workflow_sha)
    require(args.test_source_sha == TEST_SOURCE, "This campaign must use the original regression branch")
    require(1 <= args.max_runs <= 40, "Run budget must be between 1 and 40")
    require(1 <= args.timeout_minutes <= 180, "Workflow timeout must be between 1 and 180 minutes")
    return args


def main(argv=None):
    args = arguments(argv)
    if args.dry_run:
        print(json.dumps({"campaign": args.campaign, "issue": args.issue, "base_sha": args.integration_base,
                          "workflow_sha": args.workflow_sha, "workflow_ref": args.workflow_ref,
                          "test_source_sha": args.test_source_sha, "max_runs": args.max_runs,
                          "protocol": "compressed-v1",
                          "steps": ["confirm baseline", "fix worker", "restricted candidate branch",
                                    "one profile-complete CI run: focused, broad, production and selected compatibility",
                                    "three independent reviewers"],
                          "repairs": 3, "infrastructure_retries": 2, "stop_at": "ready", "integrates": False}, indent=2))
        return 0
    args.repository = args.repository.resolve()
    git(args.repository, "fetch", "--quiet", f"https://github.com/{args.repo}.git", args.workflow_sha, args.integration_base)
    with worktree(args.repository, args.workflow_sha) as control:
        manifest = json.loads((control / "scripts/bugfix/issues.json").read_text(encoding="utf-8"))
        contract = manifest["issues"].get(str(args.issue))
        require(contract is not None and contract["frozen_test_revision"] == args.test_source_sha,
                "Issue has no approved contract at the pinned workflow revision")
        result = Campaign(args, control).execute()
        print(json.dumps({"campaign": result["campaign"], "phase": result["phase"],
                          "candidate_sha": result["candidate_sha"], "repair_attempts": result["repair_attempts"],
                          "blocked_reason": result.get("blocked_reason")}, indent=2))
        return 0 if result["phase"] in ("ready", "integrated") else 1


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (Rejected, ValueError, OSError, KeyError) as error:
        print(f"bugfix-orchestrator: {error}", file=sys.stderr)
        sys.exit(1)
