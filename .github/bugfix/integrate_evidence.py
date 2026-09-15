"""Revalidate the exact per-fix profile and independent reviews before integration."""

import hashlib
import json
from artifacts import download, validate
from evidence import MATRIX, require_lane_environment
from profiles import production_evidence, profile_complete_check, validate_profile
from runs import select_artifact
from state import IDENTITY, ROLES, require
from storage import github


def completed_run(repo, run_id, workflow_sha, workflow_path):
    evidence = github(repo, f"actions/runs/{run_id}")
    require(evidence.get("status") == "completed" and evidence.get("conclusion") == "success", "Required run is not successful")
    require(evidence.get("head_sha") == workflow_sha and evidence.get("path") == workflow_path,
            "Required run has stale workflow provenance")
    require(evidence.get("event") == "workflow_dispatch", "Required run was not explicitly dispatched")
    return evidence


def _artifacts(repo, run_id):
    evidence = github(repo, f"actions/runs/{run_id}/artifacts?per_page=100")
    require(evidence["total_count"] <= 100, "Too many acceptance artifacts")
    return evidence["artifacts"]


def acceptance_evidence(repo, state):
    require(state["phase"] == "ready" and not state["paused"], "Campaign must be ready and unpaused")
    require(set(state["reviews"]) == set(ROLES), "All three independent reviews are required")
    require(len({review["run_id"] for review in state["reviews"].values()}) == 3, "Review run IDs are not independent")
    saved = {}
    kinds = ("baseline", "broad") if state.get("protocol") == "compressed-v1" else ("baseline", "focused", "broad", "acceptance")
    events = [state["evidence"][kind] for kind in kinds]
    if "acceptance_profile" in state:
        validate_profile(state["acceptance_profile"], state)
    events.extend(state["reviews"][role] for role in ROLES)
    for event in events:
        for field in IDENTITY:
            require(event.get(field) == state[field], f"Stale acceptance identity: {field}")
        if "protocol" in state:
            require(event.get("protocol") == state["protocol"], "Acceptance protocol changed")
        expected_candidate = None if event["kind"] == "baseline" else state["candidate_sha"]
        require(event.get("candidate_sha") == expected_candidate, "Stale acceptance candidate")
        workflow = "bugfix-validate.lock.yml" if event["kind"] == "review" else "bugfix-check.yml"
        definition = event.get("check_workflow_sha", state["workflow_sha"])
        if event["kind"] == "acceptance" and "check_definition" in state:
            require(definition == state["check_definition"]["workflow_sha"], "Acceptance used a stale check definition")
        if event["kind"] == "review":
            require(definition == state["workflow_sha"], "Review must retain its original worker definition")
        workflow_run = completed_run(repo, event["run_id"], definition, f".github/workflows/{workflow}")
        prefix = f"acceptance/run-{event['run_id']}"
        saved[f"{prefix}/run.json"] = (json.dumps(workflow_run, indent=2) + "\n").encode()
        available = _artifacts(repo, event["run_id"])
        profiled = profile_complete_check(state, event["kind"])
        names = state["acceptance_profile"]["required_lanes"] if profiled else (
            MATRIX if event["kind"] == "acceptance" else (event["artifact"],))
        recorded = {item["artifact"]: item for item in event.get("matrix", [])}
        if profiled:
            require(event.get("acceptance_profile") == state["acceptance_profile"] and event.get("profile_complete") is True,
                    "Profile-complete acceptance evidence is missing")
            require(set(recorded) == set(names), "Required profile lanes are missing")
            require(set(state["acceptance_profile"]["required_environments"]) <=
                    {lane["environment"] for lane in recorded.values()}, "Required profile environment is missing")
            proof, raw = production_evidence(repo, state, event["run_id"], definition)
            require(proof == event.get("production"), "Previously accepted production evidence changed")
            saved[f"{prefix}/bugfix-production.zip"] = raw
        elif event["kind"] == "acceptance":
            require(set(recorded) == set(MATRIX), "Six recorded acceptance lanes are required")
            jobs = github(repo, f"actions/runs/{event['run_id']}/jobs?per_page=100")
            require(jobs["total_count"] <= 100, "Too many acceptance jobs")
            compatibility = [job for job in jobs["jobs"] if job.get("name") == "compatibility"]
            require(len(compatibility) == 1 and compatibility[0].get("conclusion") == "success",
                    "Production-build/file-compatibility job did not pass")
            saved[f"{prefix}/jobs.json"] = (json.dumps(jobs, indent=2) + "\n").encode()
        for name in names:
            data = download(repo, select_artifact(available, name))
            lane = recorded.get(name, event)
            if event["kind"] != "review":
                require_lane_environment(name, lane["environment"])
            actual = {**event, "artifact": name, "environment": lane["environment"]}
            hashes = validate(data, actual)
            require(hashes["report_sha256"] == lane.get("report_sha256"), "Previously accepted report changed")
            saved[f"{prefix}/{name}.zip"] = data
    return saved


def evidence_manifest(files):
    return {name: {"sha256": hashlib.sha256(data).hexdigest(), "bytes": len(data)}
            for name, data in sorted(files.items())}
