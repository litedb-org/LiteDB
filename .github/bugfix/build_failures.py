"""Authenticate test and production build failures before requesting another patch."""

import hashlib
import json

from artifacts import download, read_members
from compiler_feedback import verified_failure
from profiles import production_evidence
from runs import select_artifact
from state import require
from storage import github


def test_build_failure(data, state):
    names = ("candidate/build-report.json", "candidate/build.log", "baseline/build-report.json", "scope.json")
    files = read_members(data, names)
    if "candidate/build-report.json" not in files:
        return None
    require(all(len(raw) <= 65536 for name, raw in files.items() if name.endswith(".json")), "Compiler report exceeds bounds")
    candidate = json.loads(files["candidate/build-report.json"])
    if candidate.get("outcome") == "success":
        return None
    try:
        require(set(files) == set(names), "Incomplete source-bound compiler evidence")
        baseline = json.loads(files["baseline/build-report.json"])
        expected = {"schema_version": 1, "issue": state["issue"], "source_sha": state["base_sha"],
                    "test_source_sha": state["test_source_sha"], "build_kind": "test", "framework": candidate.get("framework"),
                    "workflow_sha": state.get("check_definition", {}).get("workflow_sha", state["workflow_sha"]),
                    "returncode": 0, "timed_out": False, "outcome": "success", "diagnostics": []}
        for field, value in expected.items():
            require(type(baseline.get(field)) is type(value) and baseline[field] == value,
                    f"Baseline build identity/outcome mismatch: {field}")
        scope = json.loads(files["scope.json"])
        require(scope.get("accepted") is True and scope.get("outcome") == "scope_verified", "Candidate scope check failed")
        require(all(scope.get("provenance", {}).get(key) == state[key] for key in ("issue", "base_sha", "candidate_sha")),
                "Compiler source scope identity changed")
        return "fail", verified_failure(files[names[0]], files[names[1]], state, "test", candidate["framework"])
    except (ValueError, KeyError, TypeError) as error:
        return "harness_error", [str(error)]


def production_failure(repo, state, run_id, available):
    """Require a real failed build job and replay its source-bound diagnostic report."""
    artifact = select_artifact(available, "bugfix-production")
    data = download(repo, artifact)
    names = ("production.json", "production-build-report.json", "production-build.log")
    files = read_members(data, names)
    if "production.json" in files:
        definition = state.get("check_definition", {}).get("workflow_sha", state["workflow_sha"])
        production_evidence(repo, state, run_id, definition)
        outcome, diagnostics, report = "pass", [], files["production.json"]
    else:
        jobs = github(repo, f"actions/runs/{run_id}/jobs?per_page=100")
        require(jobs["total_count"] <= 100, "Too many production jobs")
        production = [job for job in jobs["jobs"] if job["name"] == "production"]
        require(len(production) == 1 and production[0]["status"] == "completed"
                and production[0]["conclusion"] == "failure", "No completed failed production job")
        require(names[1] in files and names[2] in files, "Production failure lacks compiler evidence")
        report = files[names[1]]
        diagnostics = verified_failure(report, files[names[2]], state, "production", "all-production-targets")
        outcome = "fail"
    return {"artifact": "bugfix-production", "artifact_id": artifact["id"], "outcome": outcome,
            "artifact_sha256": hashlib.sha256(data).hexdigest(), "report_sha256": hashlib.sha256(report).hexdigest(),
            "diagnostics": diagnostics}
