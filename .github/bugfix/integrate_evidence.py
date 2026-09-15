"""Revalidate acceptance and original-matrix evidence before integration."""

import hashlib
import json
from pathlib import Path
import sys

from artifacts import download, read_members, validate
from evidence import MATRIX
from runs import select_artifact
from state import IDENTITY, ROLES, Rejected, require
from storage import github, run


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
    events = [state["evidence"][kind] for kind in ("baseline", "focused", "broad", "acceptance")]
    events.extend(state["reviews"][role] for role in ROLES)
    for event in events:
        for field in IDENTITY:
            require(event.get(field) == state[field], f"Stale acceptance identity: {field}")
        expected_candidate = None if event["kind"] == "baseline" else state["candidate_sha"]
        require(event.get("candidate_sha") == expected_candidate, "Stale acceptance candidate")
        workflow = "bugfix-validate.lock.yml" if event["kind"] == "review" else "bugfix-check.yml"
        workflow_run = completed_run(repo, event["run_id"], state["workflow_sha"], f".github/workflows/{workflow}")
        prefix = f"acceptance/run-{event['run_id']}"
        saved[f"{prefix}/run.json"] = (json.dumps(workflow_run, indent=2) + "\n").encode()
        available = _artifacts(repo, event["run_id"])
        names = MATRIX if event["kind"] == "acceptance" else (event["artifact"],)
        recorded = {item["artifact"]: item for item in event.get("matrix", [])}
        if event["kind"] == "acceptance":
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
            actual = {**event, "artifact": name, "environment": lane["environment"]}
            hashes = validate(data, actual)
            require(hashes["report_sha256"] == lane.get("report_sha256"), "Previously accepted report changed")
            saved[f"{prefix}/{name}.zip"] = data
    return saved


def original_matrix_evidence(args, state, control, output):
    """Collect real GitHub evidence and recompute the verdict with trusted code."""
    output.mkdir(parents=True, exist_ok=True)
    archive = output / "archive"
    quarantine_path = control / ".github/bugfix/full-ci-quarantine.json"
    quarantine_bytes = quarantine_path.read_bytes()
    quarantine = json.loads(quarantine_bytes)
    require(quarantine.get("schema_version") == 1 and quarantine.get("expected_original_jobs") == 112
            and quarantine.get("expected_remaining_jobs") == 109, "Unexpected full-matrix quarantine contract")
    normalization_path = control / "scripts/bugfix/failure-normalization.json"
    normalization_bytes = normalization_path.read_bytes()
    paths = {}
    for variant, run_id in (("baseline", args.baseline_run), ("candidate", args.candidate_run)):
        path = output / f"{variant}-evidence.json"
        source = state["base_sha"] if variant == "baseline" else state["candidate_sha"]
        checked_report([sys.executable, str(control / ".github/scripts/collect_bugfix_full_ci.py"),
             "--repository", args.repo, "--run-id", str(run_id), "--issue", str(state["issue"]),
             "--role", variant,
             "--source-sha", source, "--control-root", str(control),
             "--quarantine", str(quarantine_path),
             "--failure-normalization", str(normalization_path),
             "--evidence-definition-sha", args.evidence_definition_sha, "--archive-dir", str(archive),
             "--output", str(path)], path)
        paths[variant] = path
    verdict = output / "full-ci-verdict.json"
    checked_report([sys.executable, str(control / ".github/scripts/compare_bugfix_full_ci.py"),
         "--baseline", str(paths["baseline"]), "--candidate", str(paths["candidate"]),
         "--manifest", str(control / "scripts/bugfix/issues.json"), "--issue", str(state["issue"]),
         "--base-sha", state["base_sha"], "--candidate-sha", state["candidate_sha"],
         "--quarantine", str(quarantine_path), "--failure-normalization", str(normalization_path),
         "--expected-target-job-count", "21", "--output", str(verdict)], verdict)
    report = json.loads(verdict.read_text(encoding="utf-8"))
    require(report.get("schema_version") == 1 and report.get("accepted") is True
            and report.get("outcome") == "behavior_correct", "Original-matrix comparator rejected integration")
    for field in ("errors", "blockers", "unexpected_passes"):
        require(report.get(field) == [], f"Original-matrix evidence contains {field}")
    require(report.get("coverage_gaps") == quarantine["quarantines"], "Full-matrix exclusions differ from the authorized quarantine")
    expected = {"issue": state["issue"], "baseline_run_id": args.baseline_run, "candidate_run_id": args.candidate_run,
                "base_sha": state["base_sha"], "candidate_sha": state["candidate_sha"],
                "evidence_definition_sha": args.evidence_definition_sha, "workflow_path": ".github/workflows/bugfix-full-ci.yml"}
    expected["quarantine_sha256"] = hashlib.sha256(quarantine_bytes).hexdigest()
    expected["failure_normalization_sha256"] = hashlib.sha256(normalization_bytes).hexdigest()
    provenance = report.get("provenance", {})
    for field, value in expected.items():
        require(type(provenance.get(field)) is type(value) and provenance[field] == value,
                f"Original-matrix provenance mismatch: {field}")
    files = {"original-matrix/quarantine.json": quarantine_bytes,
             "original-matrix/failure-normalization.json": normalization_bytes}
    policy = {"grading_policy_sha": args.grading_policy_sha, "capture_definition_sha": args.evidence_definition_sha,
              "comparator_report_sha256": hashlib.sha256(verdict.read_bytes()).hexdigest(), "scripts": {}}
    for name in (".github/scripts/collect_bugfix_full_ci.py", ".github/scripts/compare_bugfix_full_ci.py",
                 "scripts/bugfix/failure_normalization.py", "scripts/bugfix/trx.py"):
        policy["scripts"][name] = hashlib.sha256((control / name).read_bytes()).hexdigest()
    files["original-matrix/grading-provenance.json"] = (json.dumps(policy, indent=2) + "\n").encode()
    for path in output.rglob("*"):
        if path.is_file():
            require(not path.is_symlink() and path.resolve().is_relative_to(output.resolve()), "Unsafe evidence archive output")
            files["original-matrix/" + path.relative_to(output).as_posix()] = path.read_bytes()
    require(any(name.endswith("/run.json") for name in files), "Original-matrix API evidence was not retained")
    report["provenance"]["grading_policy_sha"] = args.grading_policy_sha
    return report, files


def checked_report(command, path):
    try:
        run(command)
    except Rejected as error:
        if path.is_file():
            report = json.loads(path.read_text(encoding="utf-8"))
            details = {key: report.get(key) for key in ("outcome", "errors", "blockers", "unexpected_passes")}
            raise Rejected("Original-matrix validation failed: " + json.dumps(details)[:6000]) from error
        raise


def evidence_manifest(files):
    return {name: {"sha256": hashlib.sha256(data).hexdigest(), "bytes": len(data)}
            for name, data in sorted(files.items())}
