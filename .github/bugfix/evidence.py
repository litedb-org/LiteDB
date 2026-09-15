"""Translate downloaded check and review reports into controller-owned events."""

import hashlib
import importlib.util
import json
import re
from collections import Counter
from pathlib import Path
import sys
import tempfile

from artifacts import download, read_members, validate, validate_worker_model
from runs import select_artifact
from profiles import profile_complete_check
from state import IDENTITY, require

MATRIX = tuple(f"bugfix-check-{os_name}-{framework}" for os_name in
               ("ubuntu-latest", "windows-latest", "macos-latest") for framework in ("net8.0", "net10.0"))


def event_for(state, kind, run_id, **fields):
    event = {key: state[key] for key in IDENTITY}
    event.update(schema_version=1, kind=kind, candidate_sha=state["candidate_sha"],
                 event_id=f"run-{run_id}-{kind}-{fields.get('role', 'ci')}", run_id=run_id)
    event.update(fields)
    if "passing_contract" in state:
        event["passing_contract"] = state["passing_contract"]
    if kind in ("baseline", "focused", "broad", "acceptance") and "check_definition" in state:
        event["check_workflow_sha"] = state["check_definition"]["workflow_sha"]
    if "protocol" in state:
        event["protocol"] = state["protocol"]
    if kind in ("candidate", "baseline", "focused", "broad", "acceptance") and "acceptance_profile" in state and "acceptance_profile" not in event:
        event["acceptance_profile"] = state["acceptance_profile"]
    return event


def broad_failure_outcome(report):
    """Unreviewed existing-failure drift needs diagnosis before another repair."""
    changes = report.get("classification_changes", [])
    if changes:
        valid = isinstance(changes, list) and all(
            isinstance(item, dict)
            and all(isinstance(item.get(field), str) and item[field].strip()
                    for field in ("name", "class_name"))
            and all(isinstance(item.get(field), str)
                    and re.fullmatch(r"[0-9a-f]{64}", item[field])
                    for field in ("baseline_failure_sha256", "candidate_failure_sha256"))
            and item["baseline_failure_sha256"] != item["candidate_failure_sha256"]
            for item in changes)
        if not valid:
            return "harness_error", ["Invalid existing-failure classification evidence"]
        return "inconclusive", changes[:8]
    if report.get("inconclusive_changes"):
        return "inconclusive", report["inconclusive_changes"][:8]
    if report.get("unexpected_passes"):
        return "inconclusive", report["unexpected_passes"][:8]
    if report.get("errors") and report.get("outcome") != "harness_error":
        return "fail", report["errors"][:8]
    return "harness_error", ["Broader execution failed or was incomplete"]


def _failure_outcome(data, control, state):
    """Only completed, correctly selected assertion results count as code failures."""
    names = ("baseline-verdict.json", "candidate/execution.json", "candidate/focused.trx")
    files = read_members(data, names)
    if set(files) != set(names):
        return "harness_error", ["Missing completed baseline/candidate test evidence"]
    baseline = json.loads(files["baseline-verdict.json"])
    if baseline.get("accepted") is not True or baseline.get("outcome") != "bug_present":
        return "harness_error", ["Baseline did not confirm the expected defect"]
    spec = importlib.util.spec_from_file_location("_bugfix_trx", control / "scripts/bugfix/trx.py")
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    execution = json.loads(files["candidate/execution.json"])
    with tempfile.TemporaryDirectory(prefix="litedb-failure-evidence-") as directory:
        path = Path(directory) / "focused.trx"
        path.write_bytes(files["candidate/focused.trx"])
        try:
            result = module.read_trx(path, execution["runs"]["focused"])
        except (module.GateError, KeyError):
            return "harness_error", ["Candidate focused test execution is incomplete or invalid"]
    contract = json.loads((control / "scripts/bugfix/issues.json").read_text(encoding="utf-8"))["issues"][str(state["issue"])]
    expected = Counter(case["name"] for case in contract["regressions"] + contract["controls"])
    if Counter(test.name for test in result.tests.values()) != expected:
        return "harness_error", ["Candidate focused test selection changed"]
    failures = [{"test": test.name, "message": test.message[:2000]}
                for test in result.tests.values() if test.outcome == "Failed"]
    if failures:
        return "fail", failures[:8]
    if state["phase"] in ("broad", "acceptance"):
        broader = read_members(data, ("broad-verdict.json",))
        if "broad-verdict.json" in broader:
            report = json.loads(broader["broad-verdict.json"])
            return broad_failure_outcome(report)
    return "harness_error", ["Focused assertions passed; broader/compatibility execution failed or was incomplete"]


def primary_artifact(state, control):
    if state["phase"] != "baseline" and "acceptance_profile" in state:
        return state["acceptance_profile"]["required_lanes"][0]
    if state["phase"] == "baseline" and control is not None:
        contract = json.loads((control / "scripts/bugfix/issues.json").read_bytes())["issues"][str(state["issue"])]
        for framework in ("net8.0", "net10.0"):
            for os_name, prefixes in (("ubuntu-latest", ("linux-x64",)), ("windows-latest", ("windows-x64",)),
                                       ("macos-latest", ("macos-x64", "macos-arm64"))):
                if any(f"{prefix}-{framework}" in contract["environments"] for prefix in prefixes):
                    return f"bugfix-check-{os_name}-{framework}"
        raise ValueError("Baseline has no supported approved environment")
    return "bugfix-check-ubuntu-latest-net8.0"


def require_lane_environment(name, environment):
    framework = name.rsplit("-", 1)[1]
    prefixes = ("linux-x64",) if "ubuntu-latest" in name else (
        ("windows-x64",) if "windows-latest" in name else ("macos-x64", "macos-arm64"))
    require(environment in {f"{prefix}-{framework}" for prefix in prefixes},
            "Artifact environment does not match its required matrix lane")


def check_event(repo, state, workflow_run, artifacts, control):
    kind = state["phase"]
    artifact_name = primary_artifact(state, control)
    event = event_for(state, kind, workflow_run["id"], artifact=artifact_name,
                      environment="linux-x64-net8.0")
    if workflow_run["conclusion"] != "success":
        if kind == "acceptance" or profile_complete_check(state, kind):
            return failed_acceptance(repo, state, workflow_run, artifacts, control, event)
        matching = [item for item in artifacts if item.get("name") == artifact_name and not item.get("expired")]
        event["outcome"] = "harness_error"
        if matching and kind != "baseline":
            data = download(repo, select_artifact(artifacts, artifact_name))
            event["outcome"], event["diagnostics"] = _failure_outcome(data, control, state)
            event["artifact_sha256"] = hashlib.sha256(data).hexdigest()
        event["reason"] = f"Check workflow concluded {workflow_run['conclusion']}"
        return event
    event["outcome"] = "bug_present" if kind == "baseline" else ("behavior_correct" if kind == "focused" else "pass")
    names = state["acceptance_profile"]["required_lanes"] if profile_complete_check(state, kind) else (
        MATRIX if kind == "acceptance" else (artifact_name,))
    matrix = []
    for name in names:
        data = download(repo, select_artifact(artifacts, name))
        verdict = json.loads(read_members(data, ("verdict.json",))["verdict.json"])
        require_lane_environment(name, verdict["environment"])
        member = {**event, "artifact": name, "environment": verdict["environment"]}
        hashes = validate(data, member)
        if name == artifact_name:
            event.update(hashes)
            event["environment"] = verdict["environment"]
        matrix.append({"artifact": name, "environment": verdict["environment"], **hashes})
    if kind == "acceptance" or profile_complete_check(state, kind):
        require(len({item["environment"] for item in matrix}) == len(names), "Acceptance environments are not distinct")
        if "acceptance_profile" in state:
            require(set(state["acceptance_profile"]["required_environments"]) <= {item["environment"] for item in matrix},
                    "Explicit required acceptance environment was not tested")
        event["matrix"] = matrix
    return event


def failed_acceptance(repo, state, workflow_run, artifacts, control, event):
    """Inspect every lane; a green representative lane cannot hide grading drift."""
    failures, matrix = [], []
    names = state["acceptance_profile"]["required_lanes"] if profile_complete_check(state, event["kind"]) else MATRIX
    for name in names:
        try:
            data = download(repo, select_artifact(artifacts, name))
            files = read_members(data, ("verdict.json", "broad-verdict.json"))
            verdict = json.loads(files["verdict.json"]) if "verdict.json" in files else {}
            if verdict.get("accepted") is True:
                require_lane_environment(name, verdict["environment"])
                validate(data, {**event, "artifact": name, "environment": verdict["environment"]})
                outcome, diagnostics = "pass", []
            else:
                outcome, diagnostics = _failure_outcome(data, control, state)
            report_name = "verdict.json" if verdict.get("accepted") is True else "broad-verdict.json"
            raw = files.get(report_name)
            environment = verdict.get("environment") or (json.loads(raw).get("provenance", {}).get("environment") if raw else None)
            matrix.append({"artifact": name, "outcome": outcome, "environment": environment,
                           "artifact_sha256": hashlib.sha256(data).hexdigest(),
                           "report_sha256": hashlib.sha256(raw).hexdigest() if raw else None})
            if outcome != "pass":
                failures.append({"artifact": name, "outcome": outcome, "diagnostics": diagnostics})
        except (ValueError, KeyError, OSError) as error:
            failures.append({"artifact": name, "outcome": "harness_error", "diagnostics": [str(error)]})
    outcomes = {item["outcome"] for item in failures}
    outcome = next((kind for kind in ("inconclusive", "harness_error", "fail") if kind in outcomes), "harness_error")
    event.update(outcome=outcome, diagnostics=failures or ["All lanes passed; workflow or compatibility failed"],
                 failed_matrix=matrix, reason=f"Check workflow concluded {workflow_run['conclusion']}")
    return event


def review_event(repo, state, workflow_run, artifacts, role):
    artifact_name = f"bugfix-review-{state['issue']}-{role}"
    event = event_for(state, "review", workflow_run["id"], role=role,
                      artifact=artifact_name, environment="independent-agent", findings=[])
    if workflow_run["conclusion"] != "success":
        event["outcome"] = "harness_error"
        return event
    data = download(repo, select_artifact(artifacts, artifact_name))
    files = read_members(data, ("result.json", "metadata.json", "runtime-proof.json"))
    result = json.loads(files["result.json"])
    metadata = json.loads(files["metadata.json"])
    validate_worker_model(metadata, role, files.get("runtime-proof.json"))
    for report in (result, metadata):
        for field in ("issue", "base_sha", "candidate_sha", "test_source_sha", "role"):
            require(type(report.get(field)) is type(event[field]) and report[field] == event[field], "Review identity mismatch")
    digest = hashlib.sha256(files["result.json"]).hexdigest()
    require(metadata.get("result_sha256") == digest and metadata.get("workflow_sha") == state["workflow_sha"]
            and metadata.get("run_id") == str(workflow_run["id"]), "Review collector provenance mismatch")
    verdict = result.get("verdict")
    require(verdict in ("pass", "changes_requested", "inconclusive"), "Unknown review verdict")
    event["outcome"] = {"pass": "pass", "changes_requested": "fail", "inconclusive": "inconclusive"}[verdict]
    event["findings"] = result.get("findings")
    if verdict == "pass":
        event.update(validate(data, event))
    else:
        require(isinstance(event["findings"], list) and event["findings"], "Negative review lacks findings")
        event.update(artifact_sha256=hashlib.sha256(data).hexdigest(), report_sha256=digest)
    return event
