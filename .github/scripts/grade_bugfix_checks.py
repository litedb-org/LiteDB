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
    for variant in (["baseline", "candidate"] if candidate else ["baseline"]):
        call([CONTROL / ".github/scripts/run_bugfix_tests.py", "--repository", ROOT / variant,
              "--output", ARTIFACTS / variant, "--manifest", MANIFEST,
              "--issue", issue, "--framework", framework, "--level", run_level])
        executions[variant] = json.loads((ARTIFACTS / variant / "execution.json").read_text())
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
               "outcome": "bug_present" if level == "baseline" else "behavior_correct"}
    (ARTIFACTS / "verdict.json").write_text(json.dumps(summary, indent=2) + "\n")
    print(json.dumps(summary))


if __name__ == "__main__":
    main()
