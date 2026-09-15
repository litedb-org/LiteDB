"""Pure, fail-closed transitions for one correctness-issue repair campaign."""

import copy
import hashlib
import json
import re

ROLES = ("behavior", "compatibility", "lifecycle")
IDENTITY = ("campaign", "issue", "base_sha", "test_source_sha", "workflow_sha")
SHA = re.compile(r"[0-9a-f]{40}\Z")
NAME = re.compile(r"[a-zA-Z0-9][a-zA-Z0-9._-]{0,79}\Z")


class Rejected(ValueError):
    """Evidence does not satisfy the campaign contract."""


def require(condition, message):
    if not condition:
        raise Rejected(message)


def new_state(campaign, issue, base_sha, test_source_sha, workflow_sha):
    require(isinstance(campaign, str) and NAME.fullmatch(campaign), "Invalid campaign name")
    require(type(issue) is int and issue > 0, "Issue must be a positive integer")
    for value in (base_sha, test_source_sha, workflow_sha):
        require(isinstance(value, str) and SHA.fullmatch(value), "Require full lowercase commit SHA")
    return {
        "schema_version": 1,
        "campaign": campaign,
        "issue": issue,
        "base_sha": base_sha,
        "test_source_sha": test_source_sha,
        "workflow_sha": workflow_sha,
        "candidate_sha": None,
        "phase": "baseline",
        "repair_attempts": 0,
        "infrastructure_retries": {},
        "events": {},
        "history": [],
        "evidence": {},
        "reviews": {},
        "paused": False,
    }


def _repair(state):
    state["phase"] = "repairing" if state["repair_attempts"] < 3 else "blocked"
    state["reviews"] = {}


def _record_result(state, event):
    kind = event["kind"]
    phase = state["phase"]
    expected = {
        "baseline": "baseline",
        "focused": "focused",
        "broad": "broad",
        "review": "reviewing",
        "acceptance": "acceptance",
    }
    require(phase == expected[kind], f"Cannot record {kind} in phase {phase}")
    require(type(event.get("run_id")) is int and event["run_id"] > 0, "Missing run ID")
    require(isinstance(event.get("environment"), str) and event["environment"], "Missing environment")
    require(isinstance(event.get("artifact"), str) and event["artifact"], "Missing evidence artifact")
    outcome = event.get("outcome")
    require(outcome in ("bug_present", "behavior_correct", "pass", "fail", "harness_error", "inconclusive"),
            "Unknown outcome")
    role = event.get("role") if kind == "review" else None
    if kind == "review":
        require(role in ROLES, "Unknown reviewer role")
        require(role not in state["reviews"], "Reviewer role already recorded")
    key = f"{kind}:{role}" if role else kind
    state["evidence"][key] = copy.deepcopy(event)
    if outcome == "harness_error":
        retries = state["infrastructure_retries"].get(key, 0) + 1
        state["infrastructure_retries"][key] = retries
        if retries > 2:
            state["phase"] = "blocked"
        return
    if outcome == "inconclusive":
        state["phase"] = "blocked"
        return
    if kind == "baseline":
        state["phase"] = "repairing" if outcome == "bug_present" else "blocked"
    elif kind == "review":
        require(outcome in ("pass", "fail"), "Review must pass or fail")
        require(isinstance(event.get("findings"), list), "Review findings must be explicit")
        if outcome == "pass":
            require(not event["findings"], "Passing review cannot contain unresolved findings")
            previous_runs = {review["run_id"] for review in state["reviews"].values()}
            require(event["run_id"] not in previous_runs, "Independent reviews require separate runs")
            state["reviews"][role] = copy.deepcopy(event)
            if set(state["reviews"]) == set(ROLES):
                state["phase"] = "acceptance"
        else:
            require(event["findings"], "Failed review must explain actionable findings")
            _repair(state)
    elif outcome in ("fail", "bug_present"):
        _repair(state)
    else:
        correct = "behavior_correct" if kind == "focused" else "pass"
        require(outcome == correct, f"{kind} requires {correct}")
        state["phase"] = {"focused": "broad", "broad": "reviewing", "acceptance": "ready"}[kind]


def apply_event(original, event):
    """Return a new state; invalid and duplicate events never partially mutate it."""
    require(original.get("schema_version") == 1, "Unknown state schema")
    require(event.get("schema_version") == 1, "Unknown event schema")
    event_id = event.get("event_id")
    require(isinstance(event_id, str) and NAME.fullmatch(event_id), "Invalid event ID")
    digest = hashlib.sha256(json.dumps(event, sort_keys=True, separators=(",", ":")).encode()).hexdigest()
    if event_id in original["events"]:
        require(original["events"][event_id] == digest, "Event ID reused with different content")
        return copy.deepcopy(original)
    for field in IDENTITY:
        require(event.get(field) == original[field], f"Stale or missing {field}")
    kind = event.get("kind")
    require(kind in ("candidate", "baseline", "focused", "broad", "review", "acceptance", "integrated", "pause", "resume"),
            "Unknown event kind")
    state = copy.deepcopy(original)
    if kind in ("pause", "resume"):
        state["paused"] = kind == "pause"
    else:
        require(not state["paused"], "Campaign is paused")
        if kind == "candidate":
            require(state["phase"] == "repairing", "Candidate requires a confirmed baseline or failed attempt")
            require(state["repair_attempts"] < 3, "Repair limit exhausted")
            candidate = event.get("candidate_sha")
            require(isinstance(candidate, str) and SHA.fullmatch(candidate), "Invalid candidate SHA")
            require(candidate != state["base_sha"] and candidate != state["candidate_sha"], "Candidate must change")
            state["candidate_sha"] = candidate
            state["repair_attempts"] += 1
            state["reviews"] = {}
            state["evidence"] = {"baseline": state["evidence"]["baseline"]}
            state["phase"] = "focused"
        else:
            require("candidate_sha" in event and event["candidate_sha"] == state["candidate_sha"],
                    "Stale or missing candidate SHA")
            if kind == "integrated":
                require(state["phase"] == "ready", "Candidate lacks complete acceptance evidence")
                require(event.get("expected_base_sha") == state["base_sha"], "Integration base moved")
                require(event.get("integration_sha") == state["candidate_sha"], "Only the tested commit may integrate")
                state["phase"] = "integrated"
                state["integration_sha"] = event["integration_sha"]
            else:
                _record_result(state, event)
    state["events"][event_id] = digest
    state["history"].append(copy.deepcopy(event))
    return state
