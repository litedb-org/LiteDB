"""Authenticate prior CI drift and the separately reviewed check definition."""

import hashlib
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile

from artifacts import download, read_members
from evidence import MATRIX
from passing import assert_passing, load_snapshot
from patching import git
from runs import select_artifact
from state import require
from storage import github

WORKFLOWS = {"bugfix-check.yml", "bugfix-controller.yml", "bugfix-full-ci.yml",
             "_bugfix-full-ci-matrix.yml", "bugfix-full-ci-build-probe.yml", "reprorunner.yml",
             "bugfix-fix.md", "bugfix-fix.lock.yml"}


def definition_diff(repository, original, proposed):
    """Record exact changed blobs and reject changes outside reviewed infrastructure."""
    require(git(repository, "merge-base", original, proposed) == original, "Check definition must descend from original runtime")
    contract = "scripts/bugfix/issues.json"
    require(git(repository, "rev-parse", f"{original}:{contract}") == git(repository, "rev-parse", f"{proposed}:{contract}"),
            "Frozen issue contract changed across check definitions")
    changes = []
    for line in git(repository, "diff", "--name-status", "--no-renames", original, proposed).splitlines():
        status, path = line.split("\t", 1)
        safe = (path.startswith((".github/bugfix/", "scripts/bugfix/", "docs/Tasks/Wholesale-Bugfix/"))
                or path.startswith(".github/scripts/") and path.endswith(".py") and
                ("bugfix" in path or "harness_overlay" in path)
                or path.startswith(".github/workflows/") and Path(path).name in WORKFLOWS)
        if path == ".gitattributes" and status == "M":
            before = git(repository, "show", f"{original}:{path}").splitlines()
            after = git(repository, "show", f"{proposed}:{path}").splitlines()
            addition = ".github/scripts/*harness_overlay*.py text eol=lf"
            safe = after.count(addition) == 1 and [line for line in after if line != addition] == before
        require(status in ("A", "M") and safe, f"Unreviewed definition path or change: {status} {path}")
        changes.append({"status": status, "path": path,
                        "old_blob": git(repository, "rev-parse", f"{original}:{path}") if status == "M" else None,
                        "new_blob": git(repository, "rev-parse", f"{proposed}:{path}")})
    require(changes, "Check definition did not change")
    return changes


def matching(actual, expected):
    require(isinstance(actual, dict), "Missing prior evidence identity")
    for key, value in expected.items():
        require(type(actual.get(key)) is type(value) and actual[key] == value, f"Prior evidence identity mismatch: {key}")


def load_trx_module(control):
    spec = importlib.util.spec_from_file_location("_revalidation_trx", control / "scripts/bugfix/trx.py")
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


def grade(control, root, operation, shared, arguments):
    output = root / f"recomputed-{operation}.json"
    result = subprocess.run([sys.executable, str(control / "scripts/bugfix/gate.py"), operation,
                             *shared, *map(str, arguments), "--output", str(output)], capture_output=True)
    require(result.returncode in (0, 1) and output.is_file(), "Original pinned gate did not produce complete evidence")
    report = json.loads(output.read_bytes())
    require(report.get("outcome") != "harness_error", "Original gate replay found a harness error")
    return report


def prior_lane(data, name, state, definition, control, snapshot, required_tests):
    names = ["scope.json", "frozen-tests.json", "passing-contract.json", "baseline-verdict.json",
             "focused-verdict.json", "broad-verdict.json"]
    names += [f"{variant}/{file}" for variant in ("baseline", "candidate")
              for file in ("execution.json", "focused.trx", "broad.trx", "test-inventory.json")]
    files = read_members(data, names)
    require(set(files) == set(names), "Prior lane lacks complete authenticated source/test evidence")
    reports = {name: json.loads(raw) for name, raw in files.items() if name.endswith(".json")}
    matching(reports["passing-contract.json"], {"snapshot": snapshot, "tests": required_tests})
    manifest = control / "scripts/bugfix/issues.json"
    manifest_sha = hashlib.sha256(manifest.read_bytes()).hexdigest()
    contract = json.loads(manifest.read_bytes())["issues"][str(state["issue"])]
    common = {"issue": state["issue"], "base_sha": state["base_sha"], "manifest_sha256": manifest_sha}
    for filename, outcome in (("scope.json", "scope_verified"), ("frozen-tests.json", "tests_unchanged")):
        matching(reports[filename], {"accepted": True, "outcome": outcome})
        matching(reports[filename]["provenance"], {**common, **({"candidate_sha": state["candidate_sha"]} if filename == "scope.json" else {})})
    changed = reports["scope.json"].get("changed_paths")
    require(isinstance(changed, list) and changed and set(changed) <= set(contract["allowed_production_paths"]),
            "Prior source scope exceeds the immutable issue contract")
    framework = name.rsplit("-", 1)[1]
    os_name = "Linux" if "ubuntu-latest" in name else ("Windows" if "windows-latest" in name else "Darwin")
    trx = load_trx_module(control)
    with tempfile.TemporaryDirectory(prefix="bugfix-prior-lane-") as directory:
        root = Path(directory)
        for filename, raw in files.items():
            path = root / filename
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(raw)
        for variant, source in (("baseline", state["base_sha"]), ("candidate", state["candidate_sha"])):
            execution = reports[f"{variant}/execution.json"]
            matching(execution, {"schema_version": 1, "source_sha": source, "test_source_sha": state["test_source_sha"],
                                 "workflow_sha": definition, "framework": framework, "runner_os": os_name})
            require(execution["runner_arch"] in ("AMD64", "x86_64", "arm64", "aarch64"), "Unknown prior runner architecture")
            assert_passing(trx.read_trx(root / variant / "broad.trx", execution["runs"]["broad"]), required_tests, variant)
        before, after = (reports[f"{variant}/execution.json"] for variant in ("baseline", "candidate"))
        require(before["runner_arch"] == after["runner_arch"], "Prior lane architecture changed")
        os_prefix = {"Linux": "linux", "Windows": "windows", "Darwin": "macos"}[os_name]
        arch = "x64" if before["runner_arch"] in ("AMD64", "x86_64") else "arm64"
        environment = f"{os_prefix}-{arch}-{framework}"
        shared = ["--manifest", str(manifest), "--issue", str(state["issue"]), "--base-sha", state["base_sha"],
                  "--test-definition-sha", state["test_source_sha"], "--environment", environment]
        baseline_args = ["--baseline-trx", root / "baseline/focused.trx", "--baseline-exit-code", before["runs"]["focused"]]
        baseline = grade(control, root, "baseline", shared, baseline_args)
        focused = grade(control, root, "focused", shared, baseline_args + ["--candidate-sha", state["candidate_sha"],
                        "--candidate-trx", root / "candidate/focused.trx", "--candidate-exit-code", after["runs"]["focused"]])
        require(baseline == reports["baseline-verdict.json"] and focused == reports["focused-verdict.json"]
                and baseline["accepted"] is True and focused["accepted"] is True, "Original focused contract replay differs")
        normalization = control / "scripts/bugfix/failure-normalization.json"
        grade(control, root, "snapshot", shared, ["--baseline-trx", root / "baseline/broad.trx",
              "--baseline-exit-code", before["runs"]["broad"], "--test-inventory", root / "baseline/test-inventory.json",
              "--allowed-failure-classes", control / "scripts/bugfix/known-failure-classes.json",
              "--failure-normalization", normalization])
        broad = grade(control, root, "compare", shared, ["--candidate-sha", state["candidate_sha"],
                      "--candidate-trx", root / "candidate/broad.trx", "--candidate-exit-code", after["runs"]["broad"],
                      "--test-inventory", root / "candidate/test-inventory.json", "--failure-normalization", normalization,
                      "--ledger", root / "recomputed-snapshot.json"])
        require(broad == reports["broad-verdict.json"], "Original broad report cannot be reproduced from raw evidence")
    changes = broad.get("classification_changes", [])
    expected_errors = [f"Known failure classification changed: {change['name']}" for change in changes]
    require(broad.get("errors") == expected_errors and broad.get("unexpected_passes") == []
            and broad.get("inconclusive_changes") == [], "Prior acceptance includes changes beyond failure-classification drift")
    require(broad.get("accepted") is (not bool(changes)), "Prior classification outcome is inconsistent")
    return {"artifact": name, "artifact_sha256": hashlib.sha256(data).hexdigest(),
            "broad_report_sha256": hashlib.sha256(files["broad-verdict.json"]).hexdigest(),
            "environment": environment, "classification_changes": changes}


def verify_prior(repo, repository, state, run_id, original_control):
    definition = state.get("check_definition", {}).get("workflow_sha", state["workflow_sha"])
    run = github(repo, f"actions/runs/{run_id}")
    matching(run, {"id": run_id, "head_sha": definition, "path": ".github/workflows/bugfix-check.yml",
                   "event": "workflow_dispatch", "status": "completed", "conclusion": "failure"})
    requests = state.get("orchestration", {}).get("requests", {})
    recorded = [request for request in requests.values() if request.get("run_id") == run_id]
    require(len(recorded) == 1, "Prior run is not one unique recorded acceptance dispatch")
    request = recorded[0]
    matching(request["inputs"], {"level": "acceptance", "issue": str(state["issue"]), "base_sha": state["base_sha"],
             "candidate_sha": state["candidate_sha"], "accepted_state_sha": state["passing_contract"]["state_commit"],
             "accepted_ledger_sha256": state["passing_contract"]["ledger_sha256"]})
    require(request["request_id"] in run.get("display_title", "").split(), "Prior acceptance request ID mismatch")
    jobs = github(repo, f"actions/runs/{run_id}/jobs?per_page=100")
    require(jobs["total_count"] <= 100, "Too many prior jobs")
    expected_jobs = {f"checks ({name.removeprefix('bugfix-check-').rsplit('-', 1)[0]}, {name.rsplit('-', 1)[1]})" for name in MATRIX}
    check_jobs = [job for job in jobs["jobs"] if job["name"].startswith("checks (")]
    require(len(check_jobs) == 6 and {job["name"] for job in check_jobs} == expected_jobs
            and all(job["status"] == "completed" and job["conclusion"] in ("success", "failure") for job in check_jobs),
            "Prior acceptance matrix is missing, cancelled or incomplete")
    snapshot, required_tests = load_snapshot(repository, repo, state["passing_contract"]["state_commit"],
                                             state["base_sha"], state["test_source_sha"])
    require(snapshot == state["passing_contract"], "Permanent passing snapshot changed")
    listing = github(repo, f"actions/runs/{run_id}/artifacts?per_page=100")
    require(listing["total_count"] <= 100, "Too many prior artifacts")
    evidence = []
    for name in MATRIX:
        artifact = select_artifact(listing["artifacts"], name)
        lane = prior_lane(download(repo, artifact), name, state, definition, original_control, snapshot, required_tests)
        lane["artifact_id"] = artifact["id"]
        evidence.append(lane)
    require(any(lane["classification_changes"] for lane in evidence), "Prior acceptance contains no proven classification drift")
    return evidence
