"""Collect and retain trusted original-matrix evidence for final promotion."""

import hashlib
import json
from pathlib import Path
import subprocess
import sys

from state import Rejected, require
from storage import run


FINAL_POLICY_PATHS = (
    ".github/workflows/bugfix-full-ci.yml",
    ".github/workflows/_bugfix-full-ci-matrix.yml",
    ".github/workflows/reprorunner.yml",
    ".github/scripts/collect_bugfix_full_ci.py",
    ".github/scripts/compare_bugfix_full_ci.py",
    ".github/scripts/compare_bugfix_final_promotion.py",
    ".github/scripts/bugfix_full_ci_quarantine.py",
    ".github/scripts/apply_issue_2794_harness_overlay.py",
    ".github/scripts/apply_issue_2825_harness_overlay.py",
    ".github/bugfix/final_evidence.py",
    ".github/bugfix/final_promotion.py",
    ".github/bugfix/passing.py",
    ".github/bugfix/patching.py",
    ".github/bugfix/state.py",
    ".github/bugfix/review_policy.py",
    ".github/bugfix/storage.py",
    ".github/bugfix/artifacts.py",
    ".github/bugfix/full-ci-quarantine.json",
    ".github/bugfix/issue-2794-harness-overlay.json",
    ".github/bugfix/issue-2794-worker-overlay.cs",
    ".github/bugfix/issue-2825-harness-overlay.json",
    ".github/bugfix/issue-2825-classifier-overlay.cs",
    "scripts/bugfix/issues.json",
    "scripts/bugfix/known-failure-classes.json",
    "scripts/bugfix/failure-normalization.json",
    "scripts/bugfix/failure_normalization.py",
    "scripts/bugfix/trx.py",
    "scripts/bugfix/source_context.py",
)

CAPTURE_DEFINITION_PATHS = (
    ".github/workflows/bugfix-full-ci.yml",
    ".github/workflows/_bugfix-full-ci-matrix.yml",
    ".github/workflows/reprorunner.yml",
    ".github/bugfix/issue-2794-harness-overlay.json",
    ".github/bugfix/issue-2794-worker-overlay.cs",
    ".github/bugfix/issue-2825-harness-overlay.json",
    ".github/bugfix/issue-2825-classifier-overlay.cs",
    ".github/scripts/apply_issue_2794_harness_overlay.py",
    ".github/scripts/apply_issue_2825_harness_overlay.py",
)


def _git_blob_sha1(data):
    header = b"blob " + str(len(data)).encode() + b"\0"
    return hashlib.sha1(header + data).hexdigest()


def _overlay(control, manifest_name, expected_issue, definition_sha):
    manifest_path = control / ".github/bugfix" / manifest_name
    raw = manifest_path.read_bytes()
    manifest = json.loads(raw)
    require(manifest.get("schema_version") == 1
            and manifest.get("issue") == expected_issue,
            f"Unexpected #{expected_issue} harness overlay contract")
    overlay_path = manifest.get("overlay_path")
    require(isinstance(overlay_path, str) and overlay_path,
            f"Missing #{expected_issue} effective overlay path")
    effective_path = (control / overlay_path).resolve()
    require(effective_path.is_relative_to(control)
            and effective_path.relative_to(control).as_posix() == overlay_path,
            f"Unsafe #{expected_issue} effective overlay path")
    effective = effective_path.read_bytes()
    require(hashlib.sha256(effective).hexdigest() == manifest.get("effective_blob_sha256"),
            f"Effective #{expected_issue} harness overlay bytes changed")
    require(_git_blob_sha1(effective) == manifest.get("effective_git_blob_sha1"),
            f"Effective #{expected_issue} harness overlay Git identity changed")
    summary = {key: value for key, value in manifest.items() if key != "schema_version"}
    summary["evidence_definition_sha"] = definition_sha
    return {
        "manifest": manifest,
        "manifest_path": manifest_path,
        "manifest_bytes": raw,
        "manifest_sha256": hashlib.sha256(raw).hexdigest(),
        "effective_path": effective_path,
        "effective_bytes": effective,
        "summary": summary,
    }


def original_matrix_evidence(args, state, control, output):
    """Collect real GitHub evidence and recompute the verdict with trusted code."""
    control, output = control.resolve(), output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    archive = output / "archive"
    quarantine_path = control / ".github/bugfix/full-ci-quarantine.json"
    quarantine_bytes = quarantine_path.read_bytes()
    quarantine = json.loads(quarantine_bytes)
    require(quarantine.get("schema_version") == 1
            and quarantine.get("expected_original_jobs") == 112
            and quarantine.get("expected_remaining_jobs") == 109,
            "Unexpected full-matrix quarantine contract")
    normalization_path = control / "scripts/bugfix/failure-normalization.json"
    normalization_bytes = normalization_path.read_bytes()
    baseline_policy_path = control / "scripts/bugfix/known-failure-classes.json"
    baseline_policy_bytes = baseline_policy_path.read_bytes()
    overlay_2794 = _overlay(control, "issue-2794-harness-overlay.json", 2794,
                            args.evidence_definition_sha)
    overlay_2825 = _overlay(control, "issue-2825-harness-overlay.json", 2825,
                            args.evidence_definition_sha)

    paths = {}
    for variant, run_id in (("baseline", args.baseline_run),
                            ("candidate", args.candidate_run)):
        path = output / f"{variant}-evidence.json"
        source = state["base_sha"] if variant == "baseline" else state["candidate_sha"]
        checked_report([
            sys.executable, str(control / ".github/scripts/collect_bugfix_full_ci.py"),
            "--repository", args.repo,
            "--run-id", str(run_id),
            "--issue", str(state["issue"]),
            "--role", variant,
            "--source-sha", source,
            "--control-root", str(control),
            "--quarantine", str(quarantine_path),
            "--failure-normalization", str(normalization_path),
            "--harness-overlay-manifest", str(overlay_2794["manifest_path"]),
            "--issue-2825-harness-overlay-manifest", str(overlay_2825["manifest_path"]),
            "--evidence-definition-sha", args.evidence_definition_sha,
            "--archive-dir", str(archive),
            "--output", str(path),
        ], path)
        paths[variant] = path

    verdict = output / "full-ci-verdict.json"
    checked_report([
        sys.executable, str(control / ".github/scripts/compare_bugfix_full_ci.py"),
        "--baseline", str(paths["baseline"]),
        "--candidate", str(paths["candidate"]),
        "--manifest", str(control / "scripts/bugfix/issues.json"),
        "--issue", str(state["issue"]),
        "--base-sha", state["base_sha"],
        "--candidate-sha", state["candidate_sha"],
        "--quarantine", str(quarantine_path),
        "--failure-normalization", str(normalization_path),
        "--baseline-policy", str(baseline_policy_path),
        "--harness-overlay-manifest", str(overlay_2794["manifest_path"]),
        "--issue-2825-harness-overlay-manifest", str(overlay_2825["manifest_path"]),
        "--expected-target-job-count", "21",
        "--output", str(verdict),
    ], verdict)
    report = json.loads(verdict.read_text(encoding="utf-8"))
    require(report.get("schema_version") == 1 and report.get("accepted") is True
            and report.get("outcome") == "behavior_correct",
            "Original-matrix comparator rejected integration")
    for field in ("errors", "blockers", "unexpected_passes", "inconclusive_changes"):
        require(report.get(field) == [], f"Original-matrix evidence contains {field}")
    require(report.get("coverage_gaps") == quarantine["quarantines"],
            "Full-matrix exclusions differ from the authorized quarantine")

    expected = {
        "issue": state["issue"],
        "baseline_run_id": args.baseline_run,
        "candidate_run_id": args.candidate_run,
        "base_sha": state["base_sha"],
        "candidate_sha": state["candidate_sha"],
        "evidence_definition_sha": args.evidence_definition_sha,
        "workflow_path": ".github/workflows/bugfix-full-ci.yml",
        "quarantine_sha256": hashlib.sha256(quarantine_bytes).hexdigest(),
        "failure_normalization_sha256": hashlib.sha256(normalization_bytes).hexdigest(),
        "baseline_policy_sha256": hashlib.sha256(baseline_policy_bytes).hexdigest(),
        "harness_overlay_manifest_sha256": overlay_2794["manifest_sha256"],
        "harness_overlay": overlay_2794["summary"],
        "issue_2825_harness_overlay_manifest_sha256": overlay_2825["manifest_sha256"],
        "issue_2825_harness_overlay": overlay_2825["summary"],
    }
    provenance = report.get("provenance", {})
    for field, value in expected.items():
        require(type(provenance.get(field)) is type(value) and provenance[field] == value,
                f"Original-matrix provenance mismatch: {field}")

    files = {
        "original-matrix/quarantine.json": quarantine_bytes,
        "original-matrix/failure-normalization.json": normalization_bytes,
        "original-matrix/baseline-policy.json": baseline_policy_bytes,
        "original-matrix/harness-overlay-manifest.json": overlay_2794["manifest_bytes"],
        "original-matrix/issue-2794-worker-overlay.cs": overlay_2794["effective_bytes"],
        "original-matrix/issue-2825-harness-overlay-manifest.json": overlay_2825["manifest_bytes"],
        "original-matrix/issue-2825-classifier-overlay.cs": overlay_2825["effective_bytes"],
    }
    policy = {
        "grading_policy_sha": args.grading_policy_sha,
        "capture_definition_sha": args.evidence_definition_sha,
        "comparator_report_sha256": hashlib.sha256(verdict.read_bytes()).hexdigest(),
        "scripts": {},
        "harness_overlay_manifest_sha256": overlay_2794["manifest_sha256"],
        "harness_overlay": overlay_2794["summary"],
        "issue_2825_harness_overlay_manifest_sha256": overlay_2825["manifest_sha256"],
        "issue_2825_harness_overlay": overlay_2825["summary"],
    }
    script_names = (
        ".github/scripts/collect_bugfix_full_ci.py",
        ".github/scripts/compare_bugfix_full_ci.py",
        "scripts/bugfix/failure_normalization.py",
        "scripts/bugfix/trx.py",
        ".github/scripts/apply_issue_2794_harness_overlay.py",
        ".github/scripts/apply_issue_2825_harness_overlay.py",
    )
    for name in script_names:
        raw = (control / name).read_bytes()
        policy["scripts"][name] = hashlib.sha256(raw).hexdigest()
        if name.startswith(".github/scripts/apply_issue_"):
            files["original-matrix/" + Path(name).name] = raw
    files["original-matrix/grading-provenance.json"] = (
        json.dumps(policy, indent=2) + "\n").encode()
    for path in output.rglob("*"):
        if path.is_file():
            require(not path.is_symlink() and path.resolve().is_relative_to(output),
                    "Unsafe evidence archive output")
            files["original-matrix/" + path.relative_to(output).as_posix()] = path.read_bytes()
    require(any(name.endswith("/run.json") for name in files),
            "Original-matrix API evidence was not retained")
    report["provenance"]["grading_policy_sha"] = args.grading_policy_sha
    return report, files


def checked_report(command, path):
    try:
        run(command)
    except Rejected as error:
        if path.is_file():
            report = json.loads(path.read_text(encoding="utf-8"))
            details = {key: report.get(key) for key in
                       ("outcome", "errors", "blockers", "unexpected_passes")}
            raise Rejected("Original-matrix validation failed: "
                           + json.dumps(details)[:6000]) from error
        raise


def retain_final_policy(control, destination, identity):
    """Copy every executable promotion input and return its byte manifest."""
    control, destination = control.resolve(), destination.resolve()
    destination.mkdir(parents=True, exist_ok=False)
    manifest = {}
    for name in FINAL_POLICY_PATHS:
        source = (control / name).resolve()
        require(source.is_relative_to(control) and source.is_file()
                and not source.is_symlink(), f"Missing trusted promotion input: {name}")
        raw = source.read_bytes()
        require(0 < len(raw) <= 8 * 1024 * 1024,
                f"Trusted promotion input exceeds bound: {name}")
        target = destination.joinpath(*Path(name).parts)
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(raw)
        manifest[name] = {"sha256": hashlib.sha256(raw).hexdigest(),
                          "bytes": len(raw)}
    identity_raw = (json.dumps(identity, indent=2, sort_keys=True) + "\n").encode()
    (destination / "promotion-identity.json").write_bytes(identity_raw)
    manifest["promotion-identity.json"] = {
        "sha256": hashlib.sha256(identity_raw).hexdigest(),
        "bytes": len(identity_raw),
    }
    manifest_raw = (json.dumps(manifest, indent=2, sort_keys=True) + "\n").encode()
    (destination / "policy-manifest.json").write_bytes(manifest_raw)
    return manifest


def retain_capture_definition(repository, definition_sha, destination):
    """Retain the exact Git blobs that executed in the capture checkout."""
    destination = destination.resolve()
    destination.mkdir(parents=True, exist_ok=False)
    manifest = {}
    for name in CAPTURE_DEFINITION_PATHS:
        entry = subprocess.run(
            ["git", "-C", str(repository), "ls-tree", definition_sha, "--", name],
            capture_output=True, check=False, text=True)
        fields = entry.stdout.strip().split()
        require(entry.returncode == 0 and len(fields) >= 3 and fields[0] == "100644"
                and fields[1] == "blob", f"Missing capture-definition blob: {name}")
        result = subprocess.run(
            ["git", "-C", str(repository), "show", f"{definition_sha}:{name}"],
            capture_output=True, check=False)
        require(result.returncode == 0 and 0 < len(result.stdout) <= 8 * 1024 * 1024,
                f"Unreadable capture-definition blob: {name}")
        target = destination.joinpath(*Path(name).parts)
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(result.stdout)
        manifest[name] = {"git_blob_sha1": fields[2],
                          "sha256": hashlib.sha256(result.stdout).hexdigest(),
                          "bytes": len(result.stdout)}
    raw = (json.dumps({"evidence_definition_sha": definition_sha,
                       "files": manifest}, indent=2,
                      sort_keys=True) + "\n").encode()
    (destination / "capture-manifest.json").write_bytes(raw)
    return manifest
