"""Audited operator path to rerun acceptance after independently reviewed grading drift."""

import argparse
import copy
import hashlib
import json
from pathlib import Path
import re
import sys

from evidence import check_event, event_for
from patching import git, worktree
from profiles import build_profile, production_evidence
from revalidation_proof import definition_diff, verify_prior
from runs import Runs
from state import ROLES, SHA, Rejected, apply_event, require
from storage import Store, verify_run


def proposal(args, state, original_control, new_control):
    require(state["candidate_sha"] == args.candidate_sha, "Explicit candidate changed")
    require(set(state["reviews"]) == set(ROLES), "All original reviews are required")
    require(state["paused"] and state["phase"] in ("acceptance", "blocked"), "Pause the campaign before authorizing revalidation")
    require(args.reason.strip(), "An explicit operator reason is required")
    for role in ROLES:
        review = state["reviews"][role]
        hashes = verify_run(args.repo, review, [".github/workflows/bugfix-validate.lock.yml"])
        require(hashes.get("report_sha256") == review.get("report_sha256"), "Original review report changed")
    prior_definition = state.get("check_definition", {}).get("workflow_sha", state["workflow_sha"])
    require(args.check_workflow_sha != prior_definition, "A newly reviewed check definition is required")
    changes = definition_diff(args.repository, state["workflow_sha"], args.check_workflow_sha)
    evidence = verify_prior(args.repo, args.repository, state, args.prior_run, original_control)
    normalization = (new_control / "scripts/bugfix/failure-normalization.json").read_bytes()
    definition = {"workflow_sha": args.check_workflow_sha, "workflow_ref": args.check_workflow_ref,
                  "normalization_sha256": hashlib.sha256(normalization).hexdigest()}
    profile = build_profile(new_control, args.repository, state)
    event = event_for(state, "revalidate", args.prior_run, reason=args.reason,
                      prior_acceptance_run_id=args.prior_run, prior_check_workflow_sha=prior_definition,
                      check_definition=definition, definition_diff=changes, classification_evidence=evidence,
                      acceptance_profile=profile,
                      review_run_ids={role: state["reviews"][role]["run_id"] for role in ROLES})
    event["event_id"] = f"revalidate-{args.check_workflow_sha[:12]}-{args.prior_run}"
    apply_event(state, event)
    return event


def execute(args):
    store = Store(args.repo)
    state, state_sha = store.read(args.campaign)
    require(state is not None, "Campaign does not exist")
    require(state["candidate_sha"] == args.candidate_sha, "Explicit candidate changed")
    prior_definition = state.get("check_definition", {}).get("workflow_sha", state["workflow_sha"])
    existing = None
    source_state = state
    if args.resume:
        existing = next((event for event in reversed(state["history"]) if event["kind"] == "revalidate"), None)
        require(existing is not None, "No authorized revalidation exists to resume")
        matching = existing["check_definition"]
        require(matching["workflow_sha"] == args.check_workflow_sha and matching["workflow_ref"] == args.check_workflow_ref
                and existing["reason"] == args.reason and existing["prior_acceptance_run_id"] == args.prior_run,
                "Resume differs from the explicit revalidation authorization")
        prior_definition = existing["prior_check_workflow_sha"]
        source_state = copy.deepcopy(state)
        source_state.update(paused=True, phase="acceptance")
        source_state["events"].pop(existing["event_id"], None)
        if prior_definition == state["workflow_sha"]:
            source_state.pop("check_definition", None)
        else:
            source_state["check_definition"] = {"workflow_sha": prior_definition}
    git(args.repository, "fetch", "--quiet", f"https://github.com/{args.repo}.git", prior_definition, args.check_workflow_sha)
    with worktree(args.repository, prior_definition) as original, worktree(args.repository, args.check_workflow_sha) as control:
        event = proposal(args, source_state, original, control)
        if existing:
            require(event == existing, "Revalidation evidence changed since authorization")
        if not args.apply:
            return {"verified": True, "applies": False, "event": event}
        if not existing:
            state = apply_event(state, event)
            state_sha = store.write(state, state_sha)
        require(not state["paused"], "Revalidation remains paused")
        if state["phase"] == "ready":
            return {"phase": "ready", "candidate_sha": state["candidate_sha"], "already_complete": True}
        require(state["phase"] == "acceptance", "Revalidation cannot run outside acceptance")
        def save():
            nonlocal state_sha
            state_sha = store.write(state, state_sha)
        runs = Runs(args.repo, args.check_workflow_ref, args.check_workflow_sha,
                    state["orchestration"], save)
        inputs = {"issue": str(state["issue"]), "base_sha": state["base_sha"], "candidate_sha": state["candidate_sha"],
                  "level": "acceptance", "accepted_state_sha": state["passing_contract"]["state_commit"],
                  "accepted_ledger_sha256": state["passing_contract"]["ledger_sha256"],
                  "acceptance_profile_sha256": state["acceptance_profile"]["profile_sha256"],
                  "protocol": state.get("protocol", "legacy-six-lane-v1")}
        # Unique audit-event namespace cannot select a cancelled original acceptance retry.
        run_id = runs.dispatch(event["event_id"], "bugfix-check.yml", inputs)
        workflow_run = runs.wait(run_id)
        latest, latest_sha = store.read(args.campaign)
        require(latest == state and latest_sha == state_sha, "Campaign changed during revalidation")
        result = check_event(args.repo, state, workflow_run, runs.artifacts(run_id), control)
        if workflow_run["conclusion"] == "success":
            result["production"], _ = production_evidence(args.repo, state, run_id, args.check_workflow_sha)
            result["profile_complete"] = True
        state = apply_event(state, result)
        state_sha = store.write(state, state_sha)
        return {"phase": state["phase"], "candidate_sha": state["candidate_sha"], "run_id": run_id,
                "state_commit": state_sha, "check_workflow_sha": args.check_workflow_sha}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("repo", "campaign", "candidate-sha", "check-workflow-sha", "check-workflow-ref", "reason"):
        parser.add_argument("--" + name, required=True)
    parser.add_argument("--prior-run", required=True, type=int)
    parser.add_argument("--repository", type=Path, default=Path.cwd())
    parser.add_argument("--apply", action="store_true", help="Record authorization and run one fresh acceptance workflow")
    parser.add_argument("--resume", action="store_true", help="Resume only the explicitly authorized revalidation dispatch")
    args = parser.parse_args()
    require(SHA.fullmatch(args.candidate_sha) and SHA.fullmatch(args.check_workflow_sha), "Full immutable SHAs required")
    require(re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_./-]*", args.check_workflow_ref)
            and ".." not in args.check_workflow_ref and "//" not in args.check_workflow_ref, "Invalid check branch")
    args.repository = args.repository.resolve()
    result = execute(args)
    print(json.dumps(result, indent=2))
    return 0 if result.get("verified") or result.get("phase") == "ready" else 1


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (Rejected, ValueError, KeyError, OSError) as error:
        print(f"bugfix-revalidation: {error}", file=sys.stderr)
        sys.exit(1)
