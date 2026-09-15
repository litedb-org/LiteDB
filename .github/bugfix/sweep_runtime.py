"""One hosted tick delegates to the immutable orchestrator and verified integrator."""

import copy
from pathlib import Path
import subprocess
import sys
import time
from types import SimpleNamespace

from errors import InfrastructureError
from integrate_storage import IntegrationStore, LOCK_NAME
from patching import git, worktree
from queue_support import inspect_queue_issue, pinned_manifest
from serial_queue import campaign_arguments
from state import Rejected, require
from storage import github
from sweep_plan import defer, dependency_blockers, select


def command(script, arguments, repository):
    # Outer hosted job is bounded too. Killing a tick never discards its journals.
    try:
        result = subprocess.run([sys.executable, str(script), *arguments], cwd=repository, check=False, timeout=360)
        return result.returncode
    except subprocess.TimeoutExpired as error:
        raise InfrastructureError("Hosted tick time budget expired; resume its durable journal") from error


def campaign_state(args, campaign):
    return IntegrationStore(args.repo).read(campaign)[0]


def drained(args, state):
    """Abandoned campaigns cannot overlap live known children with the next issue."""
    for request in state.get("orchestration", {}).get("requests", {}).values():
        if request.get("run_id"):
            run = github(args.repo, f"actions/runs/{request['run_id']}")
            require(run.get("head_sha") == state["workflow_sha"], "Abandoned run identity changed")
            if run.get("status") != "completed":
                return False
    return True


def heartbeat(manifest, owner, issue=None, state=None):
    manifest["last_tick"] = {**owner, "at": time.time(), "issue": issue,
                             "campaign": state.get("campaign") if state else None,
                             "campaign_phase": state.get("phase") if state else None,
                             "run_ids": sorted({r["run_id"] for r in state.get("orchestration", {}).get("requests", {}).values()
                                                if r.get("run_id")}) if state else [],
                             "counts": {status: sum(item["status"] == status for item in manifest["issues"].values())
                                        for status in ("pending", "active", "accepted", "deferred")}}


def reconcile_deferred(args, manifest, contracts):
    """Read-only recovery probes never clear an issue's permanent block themselves."""
    deferred = [issue for issue in manifest["specification"]["issues"]
                if manifest["issues"][str(issue)]["status"] == "deferred"]
    if not deferred:
        return
    cursor = manifest.get("recovery_cursor", 0)
    issue = deferred[cursor % len(deferred)]
    manifest["recovery_cursor"] = cursor + 1
    item = manifest["issues"][str(issue)]
    try:
        decision = inspect_queue_issue(args, issue, contracts, allow_terminal=True)
        item["last_recovery_probe"] = {"at": time.time(), "action": decision["action"]}
        if decision["action"] == "skip_accepted":
            item.update(status="accepted", decision=decision)
    except Rejected as error:
        item["last_recovery_probe"] = {"at": time.time(), "reason": str(error)[:2000]}
    for dependent in manifest["issues"].values():
        blockers = dependent.get("blocked_by", [])
        if (dependent["status"] == "deferred" and blockers and dependent.get("dependency_revisits", 0) == 0
                and all(manifest["issues"][str(blocker)]["status"] == "accepted" for blocker in blockers)):
            dependent.update(status="pending", dependency_revisits=1)
            dependent.pop("decision", None)
            manifest["phase"] = "running"


def advance(args, manifest, journal, contracts):
    expected = copy.deepcopy(manifest)
    def save(issue=None, state=None):
        nonlocal expected
        heartbeat(manifest, journal.owner, issue, state)
        expected = journal.save(manifest, expected)

    if manifest["paused"] or manifest["phase"] == "accepted_pending_final" or time.time() < manifest.get("cooldown_until", 0):
        save()
        return manifest
    spec = manifest["specification"]
    worker = SimpleNamespace(repo=args.repo, repository=args.repository, **spec)
    pinned_manifest(args.repo, spec["workflow_sha"], spec["workflow_ref"])
    if manifest["phase"] == "needs_recovery":
        reconcile_deferred(worker, manifest, contracts)
    lock, _ = IntegrationStore(args.repo).read(LOCK_NAME)
    issue = select(manifest)
    if issue is None:
        manifest["phase"] = "needs_recovery" if any(item["status"] == "deferred" for item in manifest["issues"].values()) else "accepted_pending_final"
        save()
        return manifest
    item = manifest["issues"][str(issue)]
    campaign = f"{spec['campaign_prefix']}-{issue}"
    state = None
    try:
        require(not lock or not lock.get("active") or lock.get("campaign") == campaign,
                "Another campaign owns integration; do not advance independent work")
        decision = inspect_queue_issue(worker, issue, contracts, allow_terminal=True)
        if decision["action"] == "skip_accepted":
            item.update(status="accepted", decision=decision)
        elif decision["action"] == "paused":
            manifest.update(paused=True, phase="paused", pause_reason=f"Campaign {campaign} is explicitly paused")
            state = campaign_state(worker, campaign)
        elif decision["action"] == "defer_blocked":
            state = campaign_state(worker, campaign)
            if drained(worker, state):
                defer(manifest, issue, "Campaign blocked; preserve evidence and investigate before a new approved campaign", decision, state)
            else:
                item.update(status="active", draining=True, decision=decision)
        else:
            blockers = dependency_blockers(manifest, issue, contracts) if item["status"] == "pending" else []
            if blockers:
                defer(manifest, issue, "Deferred issue shares approved production scope with an unresolved blocked issue", decision, blocked_by=blockers)
            else:
                if item.get("decision"):
                    require(item["decision"]["base_sha"] == decision["base_sha"], "Active sweep issue base changed")
                item.update(status="active", decision=decision)
                save(issue)
                git(args.repository, "fetch", "--quiet", f"https://github.com/{args.repo}.git", spec["workflow_sha"])
                with worktree(args.repository, spec["workflow_sha"]) as control:
                    if decision["phase"] == "ready":
                        options = ["--repo", args.repo, "--campaign", campaign, "--candidate-sha", decision["candidate_sha"],
                                   "--repository", str(args.repository), "--apply"]
                        if decision["resume_integration"]:
                            options.append("--resume")
                        code = command(control / ".github/bugfix/integrate.py", options, args.repository)
                    else:
                        code = command(control / ".github/bugfix/orchestrate.py", [*campaign_arguments(worker, decision), "--tick"], args.repository)
                state = campaign_state(worker, campaign)
                if code == 75:
                    raise InfrastructureError("Pinned tick or exact integration probe needs infrastructure recovery")
                if state:
                    infrastructure = sum(state.get("infrastructure_retries", {}).values()) + state.get("orchestration", {}).get("worker_retries", 0)
                    infrastructure += sum(r.get("publication_errors", 0) for r in state.get("orchestration", {}).get("requests", {}).values())
                    previous_infrastructure = item.get("observed_infrastructure_retries", 0)
                    if infrastructure > previous_infrastructure:
                        manifest["infrastructure_streak"] = manifest.get("infrastructure_streak", 0) + 1
                        manifest["cooldown_until"] = time.time() + min(86400, 300 * 2 ** min(9, manifest["infrastructure_streak"]))
                        manifest["cooldown_reason"] = "Global backoff after external worker/check infrastructure failure"
                    elif code == 0:
                        manifest["infrastructure_streak"] = 0
                    item["observed_infrastructure_retries"] = infrastructure
                if state and state.get("orchestration", {}).get("cooldown_until", 0) > time.time():
                    manifest["cooldown_until"] = state["orchestration"]["cooldown_until"]
                    manifest["cooldown_reason"] = "Authenticated AI budget/accounting cooldown; no repair or infrastructure attempt consumed"
                if state and state.get("phase") == "blocked":
                    if drained(worker, state):
                        defer(manifest, issue, "Verified campaign gates blocked progress", decision, state)
                elif state and state.get("paused"):
                    manifest.update(paused=True, phase="paused", pause_reason=f"Campaign {campaign} is explicitly paused")
                else:
                    require(code == 0, "Pinned controller tick failed; retain the exact campaign and retry boundedly")
                    item["tick_errors"] = 0
                    if state and state.get("phase") == "integrated":
                        accepted = inspect_queue_issue(worker, issue, contracts)
                        require(accepted["action"] == "skip_accepted", "Integration lacks durable accepted ledger")
                        item.update(status="accepted", decision=accepted)
    except InfrastructureError as error:
        manifest["infrastructure_streak"] = manifest.get("infrastructure_streak", 0) + 1
        manifest["cooldown_until"] = time.time() + min(86400, 300 * 2 ** min(9, manifest["infrastructure_streak"]))
        manifest["cooldown_reason"] = str(error)[:2000]
        item["last_tick_error"] = str(error)[:4000]
    except (Rejected, OSError, ValueError, KeyError, subprocess.TimeoutExpired) as error:
        # No reset of campaign blocks or per-stage budgets. One transient tick may retry.
        item["tick_errors"] += 1
        item["last_tick_error"] = str(error)[:4000]
        manifest["cooldown_until"] = time.time() + min(1800, 300 * item["tick_errors"])
        if item["tick_errors"] > 2:
            state = campaign_state(worker, campaign)
            integration, _ = IntegrationStore(args.repo).read(LOCK_NAME)
            if integration and integration.get("active"):
                item["recovery_required"] = "Resume exact integration transaction; later issues cannot bypass it"
                manifest["cooldown_until"] = time.time() + min(86400, 1800 * item["tick_errors"])
            elif state and not drained(worker, state):
                item["draining"] = True
            else:
                defer(manifest, issue, "Hosted tick retries exhausted: " + str(error)[:2000], campaign=state)
    save(issue, state)
    return manifest
