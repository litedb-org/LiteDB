"""Collect and verify the all-issue final matrix without updating a branch."""

import argparse
import hashlib
import json
from pathlib import Path
import re
import subprocess
import sys

from final_evidence import (checked_report, retain_capture_definition,
                            retain_final_policy)
from patching import git, worktree
from state import Rejected, require


SHA = re.compile(r"[0-9a-f]{40}\Z")
SHA256 = re.compile(r"[0-9a-f]{64}\Z")
DEFAULT_REPOSITORY = "litedb-org/LiteDB"


def ledger_blob(repository, state_sha):
    """Read exact accepted-ledger bytes from an immutable controller commit."""
    entry = git(repository, "ls-tree", state_sha, "--", "accepted-tests.json")
    require(entry and entry.split()[0] == "100644",
            "Accepted state commit has no ordinary accepted-tests.json blob")
    result = subprocess.run(
        ["git", "-C", str(repository), "show", f"{state_sha}:accepted-tests.json"],
        capture_output=True, check=False)
    require(result.returncode == 0 and 0 < len(result.stdout) <= 8 * 1024 * 1024,
            "Accepted ledger is unreadable or exceeds 8 MiB")
    value = json.loads(result.stdout)
    require(isinstance(value, dict) and value.get("schema_version") == 1
            and isinstance(value.get("issues"), dict) and value["issues"],
            "Accepted ledger is empty or malformed")
    return result.stdout, value


def fetch_identities(repository, remote, shas):
    for sha in sorted(set(shas)):
        require(SHA.fullmatch(sha or ""), "Final promotion requires full immutable SHAs")
        git(repository, "fetch", "--quiet", f"https://github.com/{remote}.git", sha)
        require(git(repository, "rev-parse", "--verify", sha + "^{commit}") == sha,
                f"Fetched identity is not a commit: {sha}")


def execute(args):
    repository = args.repository.resolve()
    archive = args.archive_dir.resolve() if args.archive_dir else \
        args.output.resolve().with_name(args.output.stem + "-archive")
    require(not archive.exists(), "Final-promotion archive destination already exists")
    fetch_identities(repository, args.repo, (
        args.base_sha, args.final_integration_sha, args.test_source_sha,
        args.accepted_state_sha, args.evidence_definition_sha,
        args.grading_policy_sha,
    ))
    raw_ledger, ledger = ledger_blob(repository, args.accepted_state_sha)
    # This is the canonical digest used by passing.py and carried by both runs.
    canonical = hashlib.sha256(json.dumps(
        ledger, sort_keys=True, separators=(",", ":"),
        ensure_ascii=False).encode("utf-8")).hexdigest()
    require(canonical == args.accepted_ledger_sha256,
            "Accepted ledger digest differs from the requested final capture")
    identity = {
        "schema_version": 1,
        "repository": args.repo,
        "base_sha": args.base_sha,
        "final_integration_sha": args.final_integration_sha,
        "accepted_state_sha": args.accepted_state_sha,
        "accepted_ledger_sha256": args.accepted_ledger_sha256,
        "accepted_ledger_raw_sha256": hashlib.sha256(raw_ledger).hexdigest(),
        "test_source_sha": args.test_source_sha,
        "baseline_run_id": args.baseline_run,
        "candidate_run_id": args.candidate_run,
        "evidence_definition_sha": args.evidence_definition_sha,
        "grading_policy_sha": args.grading_policy_sha,
    }
    archive.mkdir(parents=True)
    (archive / "accepted-tests.json").write_bytes(raw_ledger)
    raw_runs = archive / "raw-runs"
    raw_runs.mkdir()
    capture_manifest = retain_capture_definition(
        repository, args.evidence_definition_sha,
        archive / "capture-definition")
    with worktree(repository, args.grading_policy_sha) as control:
        retained = archive / "trusted-policy"
        policy_manifest = retain_final_policy(control, retained, identity)
        evidence_paths = {}
        for role, run_id, source_sha in (
                ("baseline", args.baseline_run, args.base_sha),
                ("candidate", args.candidate_run, args.final_integration_sha)):
            output = archive / f"{role}-evidence.json"
            checked_report([
                sys.executable,
                str(control / ".github/scripts/collect_bugfix_full_ci.py"),
                "--repository", args.repo,
                "--run-id", str(run_id),
                "--validation-scope", "final-promotion",
                "--source-sha", source_sha,
                "--role", role,
                "--final-integration-sha", args.final_integration_sha,
                "--accepted-state-sha", args.accepted_state_sha,
                "--accepted-ledger-sha256", args.accepted_ledger_sha256,
                "--test-source-sha", args.test_source_sha,
                "--evidence-definition-sha", args.evidence_definition_sha,
                "--control-root", str(control),
                "--quarantine", str(control / ".github/bugfix/full-ci-quarantine.json"),
                "--failure-normalization", str(control / "scripts/bugfix/failure-normalization.json"),
                "--harness-overlay-manifest", str(control / ".github/bugfix/issue-2794-harness-overlay.json"),
                "--issue-2825-harness-overlay-manifest", str(control / ".github/bugfix/issue-2825-harness-overlay.json"),
                "--archive-dir", str(raw_runs),
                "--output", str(output),
            ], output)
            evidence_paths[role] = output
        checked_report([
            sys.executable,
            str(control / ".github/scripts/compare_bugfix_final_promotion.py"),
            "--repository", str(repository),
            "--baseline", str(evidence_paths["baseline"]),
            "--candidate", str(evidence_paths["candidate"]),
            "--accepted-ledger", str(archive / "accepted-tests.json"),
            "--accepted-state-sha", args.accepted_state_sha,
            "--accepted-ledger-sha256", args.accepted_ledger_sha256,
            "--manifest", str(control / "scripts/bugfix/issues.json"),
            "--base-sha", args.base_sha,
            "--final-integration-sha", args.final_integration_sha,
            "--test-source-sha", args.test_source_sha,
            "--quarantine", str(control / ".github/bugfix/full-ci-quarantine.json"),
            "--failure-normalization", str(control / "scripts/bugfix/failure-normalization.json"),
            "--baseline-policy", str(control / "scripts/bugfix/known-failure-classes.json"),
            "--harness-overlay-manifest", str(control / ".github/bugfix/issue-2794-harness-overlay.json"),
            "--issue-2825-harness-overlay-manifest", str(control / ".github/bugfix/issue-2825-harness-overlay.json"),
            "--grading-policy-sha", args.grading_policy_sha,
            "--output", str(args.output.resolve()),
        ], args.output.resolve())
    report = json.loads(args.output.resolve().read_text(encoding="utf-8"))
    require(report.get("accepted") is True and report.get("outcome") == "promotion_ready",
            "Final full matrix did not authorize promotion")
    for field in ("errors", "blockers", "unexpected_passes", "inconclusive_changes"):
        require(report.get(field) == [], f"Final promotion contains {field}")
    retained_verdict = archive / "final-promotion-verdict.json"
    retained_verdict.write_bytes(args.output.resolve().read_bytes())
    archive_manifest = {}
    for path in archive.rglob("*"):
        if path.is_file():
            require(not path.is_symlink() and path.resolve().is_relative_to(archive),
                    "Unsafe final-promotion archive member")
            raw = path.read_bytes()
            archive_manifest[path.relative_to(archive).as_posix()] = {
                "sha256": hashlib.sha256(raw).hexdigest(), "bytes": len(raw)}
    manifest_path = archive / "archive-manifest.json"
    manifest_path.write_text(json.dumps(archive_manifest, indent=2,
                                        sort_keys=True) + "\n", encoding="utf-8")
    return {
        "phase": "verified",
        "accepted": True,
        "final_integration_sha": args.final_integration_sha,
        "accepted_issues": report["accepted_issues"],
        "accepted_tests": report["accepted_test_case_count"],
        "accepted_test_executions": report["accepted_test_execution_count"],
        "artifact_counts": report["artifact_counts"],
        "coverage_gaps": report["coverage_gaps"],
        "architecture_limitations": report["architecture_limitations"],
        "policy_files": len(policy_manifest),
        "capture_definition_files": len(capture_manifest),
        "archive": str(archive),
        "output": str(args.output.resolve()),
        "applies": False,
    }


def parser():
    result = argparse.ArgumentParser(description=__doc__)
    result.add_argument("--repo", default=DEFAULT_REPOSITORY)
    result.add_argument("--repository", type=Path, default=Path.cwd())
    result.add_argument("--baseline-run", type=int, required=True)
    result.add_argument("--candidate-run", type=int, required=True)
    result.add_argument("--base-sha", required=True)
    result.add_argument("--final-integration-sha", required=True)
    result.add_argument("--accepted-state-sha", required=True)
    result.add_argument("--accepted-ledger-sha256", required=True)
    result.add_argument("--test-source-sha", required=True)
    result.add_argument("--evidence-definition-sha", required=True)
    result.add_argument("--grading-policy-sha", required=True)
    result.add_argument("--archive-dir", type=Path)
    result.add_argument("--output", type=Path, required=True)
    return result


def main(argv=None):
    args = parser().parse_args(argv)
    require(re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", args.repo),
            "Invalid GitHub repository")
    require(args.baseline_run > 0 and args.candidate_run > 0
            and args.baseline_run != args.candidate_run,
            "Distinct positive full-matrix run IDs are required")
    for sha in (args.base_sha, args.final_integration_sha, args.accepted_state_sha,
                args.test_source_sha, args.evidence_definition_sha,
                args.grading_policy_sha):
        require(SHA.fullmatch(sha or ""), "Full immutable SHAs are required")
    require(SHA256.fullmatch(args.accepted_ledger_sha256 or ""),
            "Accepted ledger SHA-256 is required")
    result = execute(args)
    print(json.dumps(result, indent=2, ensure_ascii=True))


if __name__ == "__main__":
    try:
        main()
    except (Rejected, ValueError, OSError, KeyError, TypeError,
            json.JSONDecodeError) as error:
        print(f"bugfix-final-promotion: {error}", file=sys.stderr)
        sys.exit(1)
