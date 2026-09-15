"""Revalidate the exact per-fix profile and independent reviews before integration."""

import hashlib
import json
from artifacts import download, read_members, validate
from evidence import MATRIX, require_lane_environment
from profiles import production_evidence, profile_complete_check, validate_profile
from runs import select_artifact
from state import IDENTITY, ROLES, require
from storage import github


def completed_run(repo, run_id, workflow_sha, workflow_path):
    evidence = github(repo, f"actions/runs/{run_id}")
    require_completed_run(evidence, workflow_sha, workflow_path)
    return evidence


def require_completed_run(evidence, workflow_sha, workflow_path):
    require(evidence.get("status") == "completed" and evidence.get("conclusion") == "success", "Required run is not successful")
    require(evidence.get("head_sha") == workflow_sha and evidence.get("path") == workflow_path,
            "Required run has stale workflow provenance")
    require(evidence.get("event") == "workflow_dispatch", "Required run was not explicitly dispatched")


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


def validate_archived_evidence(saved, state):
    """Revalidate prepared raw archives without depending on Actions retention or availability."""
    require(state["phase"] == "ready" and not state["paused"], "Campaign must be ready and unpaused")
    require(set(state["reviews"]) == set(ROLES), "All three independent reviews are required")
    require(len({review["run_id"] for review in state["reviews"].values()}) == 3,
            "Archived review run IDs are not independent")
    if "acceptance_profile" in state:
        validate_profile(state["acceptance_profile"], state)
    kinds = ("baseline", "broad") if state.get("protocol") == "compressed-v1" else (
        "baseline", "focused", "broad", "acceptance")
    events = [state["evidence"][kind] for kind in kinds]
    events.extend(state["reviews"][role] for role in ROLES)
    for event in events:
        for field in IDENTITY:
            require(event.get(field) == state[field], f"Stale archived acceptance identity: {field}")
        if "protocol" in state:
            require(event.get("protocol") == state["protocol"], "Archived acceptance protocol changed")
        expected_candidate = None if event["kind"] == "baseline" else state["candidate_sha"]
        require(event.get("candidate_sha") == expected_candidate, "Stale archived acceptance candidate")
        workflow = "bugfix-validate.lock.yml" if event["kind"] == "review" else "bugfix-check.yml"
        definition = event.get("check_workflow_sha", state["workflow_sha"])
        if event["kind"] == "acceptance" and "check_definition" in state:
            require(definition == state["check_definition"]["workflow_sha"],
                    "Archived acceptance used a stale check definition")
        if event["kind"] == "review":
            require(definition == state["workflow_sha"],
                    "Archived review did not retain its original worker definition")
        prefix = f"acceptance/run-{event['run_id']}"
        run_name = f"{prefix}/run.json"
        require(run_name in saved, "Prepared evidence lacks run provenance")
        run = json.loads(saved[run_name])
        require(run.get("id") == event["run_id"], "Archived run ID changed")
        require_completed_run(run, definition, f".github/workflows/{workflow}")
        profiled = profile_complete_check(state, event["kind"])
        names = state["acceptance_profile"]["required_lanes"] if profiled else (
            MATRIX if event["kind"] == "acceptance" else (event["artifact"],))
        recorded = {item["artifact"]: item for item in event.get("matrix", [])}
        if profiled:
            require(event.get("acceptance_profile") == state["acceptance_profile"] and
                    event.get("profile_complete") is True, "Archived profile-complete evidence is missing")
            require(set(recorded) == set(names), "Archived profile lanes are missing")
            require(set(state["acceptance_profile"]["required_environments"]) <=
                    {lane["environment"] for lane in recorded.values()},
                    "Archived required profile environment is missing")
            production_name = f"{prefix}/bugfix-production.zip"
            require(production_name in saved, "Prepared evidence lacks production proof")
            production = event.get("production", {})
            raw = read_members(saved[production_name], ("production.json",)).get("production.json")
            require(raw is not None and production.get("artifact_sha256") ==
                    hashlib.sha256(saved[production_name]).hexdigest(), "Archived production artifact changed")
            require(production.get("report_sha256") == hashlib.sha256(raw).hexdigest(),
                    "Archived production report changed")
            expected_production = {"schema_version": 1, "issue": state["issue"],
                                   "base_sha": state["base_sha"], "candidate_sha": state["candidate_sha"],
                                   "workflow_sha": definition, "acceptance_profile": state["acceptance_profile"],
                                   "accepted": True, "production_build": True,
                                   "compatibility": state["acceptance_profile"]["compatibility"]}
            require(json.loads(raw) == expected_production, "Archived production verdict changed")
        for name in names:
            archive_name = f"{prefix}/{name}.zip"
            require(archive_name in saved, "Prepared evidence lacks an accepted raw artifact")
            lane = recorded.get(name, event)
            if event["kind"] != "review":
                require_lane_environment(name, lane["environment"])
            actual = {**event, "artifact": name, "environment": lane["environment"]}
            hashes = validate(saved[archive_name], actual)
            require(hashes["report_sha256"] == lane.get("report_sha256"),
                    "Archived accepted report changed")
            if "artifact_sha256" in lane:
                require(hashes["artifact_sha256"] == lane["artifact_sha256"],
                        "Archived accepted artifact changed")
        if event["kind"] == "acceptance" and not profiled:
            jobs_name = f"{prefix}/jobs.json"
            require(jobs_name in saved, "Prepared evidence lacks compatibility job provenance")
            jobs = json.loads(saved[jobs_name])
            require(jobs.get("total_count", 101) <= 100, "Archived acceptance has too many jobs")
            compatibility = [job for job in jobs.get("jobs", []) if job.get("name") == "compatibility"]
            require(len(compatibility) == 1 and compatibility[0].get("conclusion") == "success",
                    "Archived compatibility job did not pass")


def evidence_manifest(files):
    return {name: {"sha256": hashlib.sha256(data).hexdigest(), "bytes": len(data)}
            for name, data in sorted(files.items())}
