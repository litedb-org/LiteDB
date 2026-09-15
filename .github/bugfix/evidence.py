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


def check_event(repo, state, workflow_run, artifacts, control):
    kind = state["phase"]
    artifact_name = "bugfix-check-ubuntu-latest-net8.0"
    event = event_for(state, kind, workflow_run["id"], artifact=artifact_name,
                      environment="linux-x64-net8.0")
    if workflow_run["conclusion"] != "success":
        matching = [item for item in artifacts if item.get("name") == artifact_name and not item.get("expired")]
        event["outcome"] = "harness_error"
        if matching and kind != "baseline":
            data = download(repo, select_artifact(artifacts, artifact_name))
            event["outcome"], event["diagnostics"] = _failure_outcome(data, control, state)
            event["artifact_sha256"] = hashlib.sha256(data).hexdigest()
        event["reason"] = f"Check workflow concluded {workflow_run['conclusion']}"
        return event
    event["outcome"] = "bug_present" if kind == "baseline" else ("behavior_correct" if kind == "focused" else "pass")
    names = MATRIX if kind == "acceptance" else (artifact_name,)
    matrix = []
    for name in names:
        data = download(repo, select_artifact(artifacts, name))
        verdict = json.loads(read_members(data, ("verdict.json",))["verdict.json"])
        framework = name.rsplit("-", 1)[1]
        prefixes = ("linux-x64",) if "ubuntu-latest" in name else (
            ("windows-x64",) if "windows-latest" in name else ("macos-x64", "macos-arm64"))
        require(verdict["environment"] in {f"{prefix}-{framework}" for prefix in prefixes},
                "Artifact environment does not match its required matrix lane")
        member = {**event, "artifact": name, "environment": verdict["environment"]}
        hashes = validate(data, member)
        if name == artifact_name:
            event.update(hashes)
        matrix.append({"artifact": name, "environment": verdict["environment"], **hashes})
    if kind == "acceptance":
        require(len({item["environment"] for item in matrix}) == 6, "Acceptance environments are not distinct")
        event["matrix"] = matrix
    return event


def review_event(repo, state, workflow_run, artifacts, role):
    artifact_name = f"bugfix-review-{state['issue']}-{role}"
    event = event_for(state, "review", workflow_run["id"], role=role,
                      artifact=artifact_name, environment="independent-agent", findings=[])
    if workflow_run["conclusion"] != "success":
        event["outcome"] = "harness_error"
        return event
    data = download(repo, select_artifact(artifacts, artifact_name))
    files = read_members(data, ("result.json", "metadata.json"))
    result = json.loads(files["result.json"])
    metadata = json.loads(files["metadata.json"])
    validate_worker_model(metadata, role)
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
