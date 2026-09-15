"""Persist and advance an explicitly approved sweep from short hosted Actions ticks."""

import argparse
import copy
import json
from pathlib import Path
import re
import sys
import time

from patching import git
from queue_support import pinned_manifest
from state import Rejected, require
from sweep_plan import create, specification, validate
from sweep_runtime import advance, heartbeat
from sweep_store import SweepStore


def arguments(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("operation", choices=("init", "tick", "pause", "resume", "status"))
    parser.add_argument("--repo", required=True)
    parser.add_argument("--sweep", required=True)
    parser.add_argument("--owner-run-id", type=int)
    parser.add_argument("--owner-run-attempt", type=int, default=1)
    parser.add_argument("--repository", type=Path, default=Path.cwd())
    parser.add_argument("--scheduler-sha")
    parser.add_argument("--workflow-sha")
    parser.add_argument("--workflow-ref")
    parser.add_argument("--campaign-prefix")
    parser.add_argument("--issues", nargs="+", type=int)
    parser.add_argument("--max-runs", type=int, default=40)
    parser.add_argument("--timeout-minutes", type=int, default=180)
    parser.add_argument("--reason")
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args(argv)
    require(re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", args.repo), "Invalid repository")
    require(args.operation != "init" or all((args.workflow_sha, args.workflow_ref, args.campaign_prefix, args.issues)),
            "Initialization requires immutable runtime, campaign prefix and explicit approved issues")
    require(args.operation not in ("pause", "resume") or args.reason and len(args.reason.strip()) >= 5,
            "An auditable pause/resume reason is required")
    args.repository = args.repository.resolve()
    return args


def execute(args):
    journal = SweepStore(args.repo, args.sweep, args.owner_run_id, args.owner_run_attempt, args.scheduler_sha)
    existing, _ = journal.read()
    if args.operation == "status":
        require(existing is not None, "Sweep does not exist")
        return existing
    require(args.scheduler_sha and git(Path(__file__).resolve().parents[2], "rev-parse", "HEAD") == args.scheduler_sha,
            "Executing scheduler checkout differs from its immutable pin")
    if args.operation == "init":
        contracts = pinned_manifest(args.repo, args.workflow_sha, args.workflow_ref)["issues"]
        proposed = create(args, contracts)
        if existing:
            require(existing["specification"] == proposed["specification"], "Existing sweep identity or scope differs")
    else:
        require(existing is not None, "Sweep does not exist")
        spec = existing["specification"]
        require(spec.get("scheduler_sha") == args.scheduler_sha, "Scheduler pin differs from the initialized sweep")
        contracts = pinned_manifest(args.repo, spec["workflow_sha"], spec["workflow_ref"])["issues"]
        validate(existing, args.sweep, contracts)
    if args.dry_run:
        return {"dry_run": True, "mutations": False, "operation": args.operation,
                "manifest": proposed if args.operation == "init" else existing}
    journal.acquire()
    try:
        _, current, _ = journal.locked_snapshot()
        require(current == existing, "Sweep changed before lease acquisition")
        if existing:
            require(existing.get("bootstrap_blob_sha") == journal.bootstrap_blob_sha, "Bootstrap workflow changed since sweep initialization")
        if args.operation == "init":
            if not existing:
                proposed["bootstrap_blob_sha"] = journal.bootstrap_blob_sha
                heartbeat(proposed, journal.owner)
                journal.save(proposed, None)
                return proposed
            return existing
        if args.operation == "tick":
            return advance(args, current, journal, contracts)
        changed = copy.deepcopy(current)
        require(changed["phase"] != "accepted_pending_final", "Accepted sweep awaits separate final validation")
        changed.update(paused=args.operation == "pause", phase="paused" if args.operation == "pause" else "running")
        changed["history"].append({"action": args.operation, "reason": args.reason[:4000], "owner": journal.owner, "at": time.time()})
        heartbeat(changed, journal.owner)
        journal.save(changed, current)
        return changed
    finally:
        journal.release()


def main(argv=None):
    result = execute(arguments(argv))
    print(json.dumps(result, indent=2, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (Rejected, ValueError, OSError, KeyError, TypeError) as error:
        print(f"bugfix-sweep: {error}", file=sys.stderr)
        sys.exit(1)
