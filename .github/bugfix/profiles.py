"""Bind trusted acceptance plans to candidate events and CI evidence."""

import hashlib
import importlib.util
import json
import sys

from state import require


def validate_profile(profile, state):
    require(isinstance(profile, dict), "Missing acceptance profile")
    for field in ("issue", "base_sha", "candidate_sha"):
        require(type(profile.get(field)) is type(state[field]) and profile[field] == state[field], "Acceptance profile identity changed")
    raw = {key: value for key, value in profile.items() if key != "profile_sha256"}
    digest = hashlib.sha256(json.dumps(raw, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode()).hexdigest()
    require(profile.get("profile_sha256") == digest, "Acceptance profile digest mismatch")
    require(profile.get("production_build") is True and type(profile.get("compatibility")) is bool,
            "Acceptance profile must explicitly require production build")
    lanes = profile.get("required_lanes")
    allowed = {f"bugfix-check-{os_name}-{framework}" for os_name in ("ubuntu-latest", "windows-latest", "macos-latest")
               for framework in ("net8.0", "net10.0")}
    require(isinstance(lanes, list) and lanes and len(lanes) == len(set(lanes)) and set(lanes) <= allowed,
            "Invalid required acceptance lanes")
    return profile


def build_profile(control, repository, state, candidate=None):
    source = control / ".github/bugfix/acceptance_profile.py"
    spec = importlib.util.spec_from_file_location("_trusted_profile", source)
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    contract = json.loads((control / "scripts/bugfix/issues.json").read_bytes())["issues"][str(state["issue"])]
    candidate = candidate or state["candidate_sha"]
    result = module.plan(repository, state["base_sha"], candidate, contract)
    return validate_profile(result, {**state, "candidate_sha": candidate})


def observe_source_context(control, repository, state):
    contract = json.loads((control / "scripts/bugfix/issues.json").read_bytes())["issues"][str(state["issue"])]
    if not contract.get("source_context_observations"):
        return []
    spec = importlib.util.spec_from_file_location("_trusted_source_context", control / "scripts/bugfix/source_context.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module.observe(repository, contract, state["base_sha"], state["candidate_sha"])


def profile_complete_check(state, kind):
    return "acceptance_profile" in state and (kind == "acceptance" or
           kind == "broad" and state.get("protocol") == "compressed-v1")


def targeted_coverage(run, filters):
    coverage = {}
    for selection in filters:
        require(isinstance(selection, str) and selection.startswith("FullyQualifiedName~"), "Unsupported targeted profile filter")
        name = selection.removeprefix("FullyQualifiedName~")
        require(name, "Empty targeted profile filter")
        cases = [case for case in run.tests.values() if name in case.name]
        executed = [case for case in cases if case.outcome in ("Passed", "Failed")]
        require(cases and executed, f"Required targeted tests are missing or all skipped: {selection}")
        coverage[selection] = {"total": len(cases), "executed": len(executed)}
    return coverage


def production_evidence(repo, state, run_id, definition):
    from artifacts import download, read_members
    from runs import select_artifact
    from storage import github
    jobs = github(repo, f"actions/runs/{run_id}/jobs?per_page=100")
    require(jobs["total_count"] <= 100, "Too many production jobs")
    required = [job for job in jobs["jobs"] if job["name"] == "production"]
    require(len(required) == 1 and required[0]["status"] == "completed" and required[0]["conclusion"] == "success",
            "Required production/compatibility job did not pass")
    artifacts = github(repo, f"actions/runs/{run_id}/artifacts?per_page=100")
    require(artifacts["total_count"] <= 100, "Too many production artifacts")
    artifact = select_artifact(artifacts["artifacts"], "bugfix-production")
    data = download(repo, artifact)
    raw = read_members(data, ("production.json",)).get("production.json")
    require(raw is not None, "Missing production verdict")
    report = json.loads(raw)
    expected = {"schema_version": 1, "issue": state["issue"], "base_sha": state["base_sha"],
                "candidate_sha": state["candidate_sha"], "workflow_sha": definition,
                "acceptance_profile": state["acceptance_profile"], "accepted": True,
                "production_build": True, "compatibility": state["acceptance_profile"]["compatibility"]}
    require(report == expected, "Production verdict differs from required source/profile checks")
    return {"artifact": "bugfix-production", "artifact_id": artifact["id"],
            "artifact_sha256": hashlib.sha256(data).hexdigest(), "report_sha256": hashlib.sha256(raw).hexdigest()}, data
