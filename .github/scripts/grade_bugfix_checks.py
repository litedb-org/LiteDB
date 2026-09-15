"""Run the trusted bugfix gate against separately checked-out source revisions."""

import json
import os
from pathlib import Path
import platform
import subprocess
import sys


ROOT = Path.cwd()
CONTROL = ROOT / "control"
ARTIFACTS = ROOT / "artifacts"
MANIFEST = CONTROL / "scripts/bugfix/issues.json"
GATE = CONTROL / "scripts/bugfix/gate.py"
FAILURE_NORMALIZATION = CONTROL / "scripts/bugfix/failure-normalization.json"


def call(arguments):
    subprocess.run([sys.executable, *map(str, arguments)], check=True)


def main():
    issue = os.environ["ISSUE"]
    base = os.environ["BASE_SHA"]
    candidate = os.environ.get("CANDIDATE_SHA", "")
    level = os.environ["LEVEL"]
    framework = os.environ["FRAMEWORK"]
    test_source = json.loads(MANIFEST.read_text(encoding="utf-8"))["issues"][issue]["frozen_test_revision"]
    os_name = {"Linux": "linux", "Windows": "windows", "Darwin": "macos"}[platform.system()]
    arch = {"AMD64": "x64", "x86_64": "x64", "arm64": "arm64", "aarch64": "arm64"}[platform.machine()]
    environment = f"{os_name}-{arch}-{framework}"
    ARTIFACTS.mkdir(exist_ok=False)
    sys.path.insert(0, str(CONTROL / ".github/bugfix"))
    from passing import assert_passing, load_snapshot, selection_filter
    from profiles import build_profile, targeted_coverage
    sys.path.insert(0, str(CONTROL / "scripts/bugfix"))
    from trx import read_trx
    profile = None
    if candidate:
        profile = build_profile(CONTROL, ROOT / "candidate", {"issue": int(issue), "base_sha": base, "candidate_sha": candidate})
        if profile["profile_sha256"] != os.environ.get("ACCEPTANCE_PROFILE_SHA256"):
            raise ValueError("Candidate acceptance profile differs from immutable dispatch")
        (ARTIFACTS / "acceptance-profile.json").write_text(json.dumps(profile, indent=2) + "\n", encoding="utf-8")
    passing_contract, required_tests = load_snapshot(ROOT / "baseline", os.environ["GITHUB_REPOSITORY"],
                                                    os.environ["ACCEPTED_STATE_SHA"], base, test_source)
    if passing_contract["ledger_sha256"] != os.environ["ACCEPTED_LEDGER_SHA256"]:
        raise ValueError("Accepted ledger differs from the campaign's immutable snapshot")
    (ARTIFACTS / "passing-contract.json").write_text(
        json.dumps({"snapshot": passing_contract, "tests": required_tests}, indent=2) + "\n", encoding="utf-8")
    shared = ["--manifest", MANIFEST, "--issue", issue, "--base-sha", base]
    provenance = ["--environment", environment, "--test-definition-sha", test_source]
    call([GATE, "verify-tests", *shared, "--repository", ROOT / "baseline",
          "--output", ARTIFACTS / "frozen-tests.json"])
    if level != "baseline" and not candidate:
        raise ValueError("A candidate SHA is required outside baseline mode")
    if candidate:
        call([GATE, "protect", *shared, "--candidate-sha", candidate,
              "--repository", ROOT / "candidate", "--output", ARTIFACTS / "scope.json"])
    run_level = "broad" if level in ("broad", "acceptance") else "focused"
    executions = {}
    coverage = {}
    for variant in (["baseline", "candidate"] if candidate else ["baseline"]):
        required_lane = ["--required-pass-filter", selection_filter(required_tests)] if required_tests and run_level == "focused" else []
        call([CONTROL / ".github/scripts/run_bugfix_tests.py", "--repository", ROOT / variant,
              "--output", ARTIFACTS / variant, "--manifest", MANIFEST,
              "--issue", issue, "--framework", framework, "--level", run_level, *required_lane])
        executions[variant] = json.loads((ARTIFACTS / variant / "execution.json").read_text())
        targeted = profile["targeted_test_filters"] if profile and run_level == "broad" else []
        if required_tests or targeted:
            lane = "broad" if run_level == "broad" else "required-pass"
            completed = read_trx(ARTIFACTS / variant / f"{lane}.trx", executions[variant]["runs"][lane])
            assert_passing(completed, required_tests, variant)
            if targeted:
                coverage[variant] = targeted_coverage(completed, targeted)
    baseline_args = ["--baseline-trx", ARTIFACTS / "baseline/focused.trx",
                     "--baseline-exit-code", executions["baseline"]["runs"]["focused"]]
    call([GATE, "baseline", *shared, *provenance, *baseline_args,
          "--output", ARTIFACTS / "baseline-verdict.json"])
    if candidate:
        call([GATE, "focused", *shared, *provenance, *baseline_args,
              "--candidate-sha", candidate,
              "--candidate-trx", ARTIFACTS / "candidate/focused.trx",
              "--candidate-exit-code", executions["candidate"]["runs"]["focused"],
              "--output", ARTIFACTS / "focused-verdict.json"])
    if run_level == "broad":
        call([GATE, "snapshot", *shared, *provenance,
              "--baseline-trx", ARTIFACTS / "baseline/broad.trx",
              "--baseline-exit-code", executions["baseline"]["runs"]["broad"],
              "--test-inventory", ARTIFACTS / "baseline/test-inventory.json",
              "--allowed-failure-classes", CONTROL / "scripts/bugfix/known-failure-classes.json",
              "--failure-normalization", FAILURE_NORMALIZATION,
              "--output", ARTIFACTS / "baseline-ledger.json"])
        call([GATE, "compare", *shared, *provenance,
              "--candidate-sha", candidate, "--ledger", ARTIFACTS / "baseline-ledger.json",
              "--candidate-trx", ARTIFACTS / "candidate/broad.trx",
              "--candidate-exit-code", executions["candidate"]["runs"]["broad"],
              "--test-inventory", ARTIFACTS / "candidate/test-inventory.json",
              "--failure-normalization", FAILURE_NORMALIZATION,
              "--output", ARTIFACTS / "broad-verdict.json"])
    summary = {"schema_version": 1, "accepted": True, "issue": int(issue),
               "base_sha": base, "candidate_sha": candidate or None,
               "test_source_sha": test_source, "workflow_sha": os.environ["GITHUB_SHA"],
               "environment": environment, "level": level,
               "passing_contract": passing_contract, "previously_accepted_tests_passed": True,
               "outcome": "bug_present" if level == "baseline" else "behavior_correct"}
    if profile is not None:
        summary["acceptance_profile"] = profile
        summary["targeted_test_coverage"] = coverage
    summary["protocol"] = os.environ.get("PROTOCOL", "legacy-six-lane-v1")
    (ARTIFACTS / "verdict.json").write_text(json.dumps(summary, indent=2) + "\n")
    print(json.dumps(summary))


if __name__ == "__main__":
    main()
