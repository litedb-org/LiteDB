"""Explicit immutable sweep scope and durable dispositions; no issue discovery."""

import hashlib
import json
import time
import re
from types import SimpleNamespace

from orchestrate import TEST_SOURCE
from state import NAME, SHA, require

TERMINAL = {"accepted", "deferred"}


def digest(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode()).hexdigest()


def specification(args, contracts):
    require(NAME.fullmatch(args.sweep) and len(args.sweep) <= 60, "Invalid sweep name")
    require(SHA.fullmatch(args.scheduler_sha), "Full immutable scheduler SHA required")
    require(SHA.fullmatch(args.workflow_sha), "Full immutable runtime SHA required")
    require(1 <= len(args.issues) <= 40 and len(set(args.issues)) == len(args.issues)
            and all(type(issue) is int and issue > 0 for issue in args.issues), "One to forty distinct approved issues required")
    require(all(NAME.fullmatch(f"{args.campaign_prefix}-{issue}") for issue in args.issues), "Invalid campaign prefix")
    require(1 <= args.max_runs <= 40 and 1 <= args.timeout_minutes <= 180, "Invalid campaign bounds")
    require(re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_./-]*", args.workflow_ref)
            and ".." not in args.workflow_ref and "//" not in args.workflow_ref, "Invalid runtime ref")
    hashes = {}
    for issue in args.issues:
        contract = contracts.get(str(issue))
        require(contract and contract.get("inventory_issue") == issue and contract.get("frozen_test_revision") == TEST_SOURCE
                and contract.get("regressions") and contract.get("controls") and contract.get("allowed_production_paths"),
                f"Issue {issue} has no complete approved contract at this runtime")
        hashes[str(issue)] = digest(contract)
    return {"scheduler_sha": args.scheduler_sha, "workflow_sha": args.workflow_sha, "workflow_ref": args.workflow_ref, "campaign_prefix": args.campaign_prefix,
            "test_source_sha": TEST_SOURCE, "issues": args.issues, "contract_sha256": hashes,
            "max_runs": args.max_runs, "timeout_minutes": args.timeout_minutes}


def create(args, contracts):
    return {"schema_version": 1, "kind": "hosted-bugfix-sweep", "sweep": args.sweep,
            "specification": specification(args, contracts), "paused": False, "phase": "running",
            "issues": {str(issue): {"status": "pending", "tick_errors": 0} for issue in args.issues},
            "history": [{"action": "initialize", "at": time.time()}], "cooldown_until": 0}


def validate(manifest, sweep, contracts):
    require(manifest and manifest.get("schema_version") == 1 and manifest.get("kind") == "hosted-bugfix-sweep"
            and manifest.get("sweep") == sweep, "Invalid sweep manifest")
    spec = manifest["specification"]
    require(spec == specification(SimpleNamespace(sweep=sweep, **spec), contracts), "Sweep specification changed or is invalid")
    require(type(manifest.get("paused")) is bool and manifest.get("phase") in ("running", "accepted_pending_final", "needs_recovery", "paused"), "Invalid sweep status")
    require(set(manifest["issues"]) == {str(issue) for issue in spec["issues"]}, "Sweep issue inventory changed")
    for issue in spec["issues"]:
        require(spec["contract_sha256"][str(issue)] == digest(contracts[str(issue)]), "Pinned sweep contract changed")
        require(manifest["issues"][str(issue)]["status"] in ("pending", "active", "accepted", "deferred"), "Unknown issue disposition")
    require(sum(item["status"] == "active" for item in manifest["issues"].values()) <= 1, "More than one issue is active")


def dependency_blockers(manifest, issue, contracts):
    paths = set(contracts[str(issue)]["allowed_production_paths"])
    return [int(number) for number, item in manifest["issues"].items()
            if item["status"] == "deferred" and paths.intersection(contracts[number]["allowed_production_paths"])]


def defer(manifest, issue, reason, decision=None, campaign=None, blocked_by=None):
    item = manifest["issues"][str(issue)]
    item.update(status="deferred", reason=reason, deferred_at=time.time(), blocked_by=blocked_by or [])
    if decision:
        item["decision"] = decision
    if campaign:
        item["investigation"] = {"campaign": campaign["campaign"], "phase": campaign["phase"],
                                "base_sha": campaign["base_sha"], "candidate_sha": campaign.get("candidate_sha"),
                                "repair_attempts": campaign.get("repair_attempts"),
                                "last_event": campaign.get("history", [])[-1:]}
    manifest["history"].append({"action": "defer", "issue": issue, "reason": reason, "at": time.time()})


def select(manifest):
    for status in ("active", "pending"):
        for issue in manifest["specification"]["issues"]:
            if manifest["issues"][str(issue)]["status"] == status:
                return issue
    return None
