#!/usr/bin/env python3
"""Download and normalize trusted evidence from a Bugfix Full CI run."""

import argparse
from collections import Counter
import copy
import hashlib
import importlib.util
import io
import json
from pathlib import Path
from pathlib import PurePosixPath
import re
import stat
import subprocess
import sys
import tempfile
import zipfile

from bugfix_full_ci_quarantine import load_quarantine
from apply_issue_2794_harness_overlay import (
    load_manifest_path as load_issue_2794_harness_overlay,
    provenance_contract as issue_2794_harness_overlay_contract,
)
from apply_issue_2825_harness_overlay import (
    load_manifest_path as load_issue_2825_harness_overlay,
    provenance_contract as issue_2825_harness_overlay_contract,
)

# Retain the original helper names for focused unit tests and older local callers.
load_harness_overlay = load_issue_2794_harness_overlay
harness_overlay_contract = issue_2794_harness_overlay_contract


SHA = re.compile(r"[0-9a-f]{40}")
SDK_KEYS = {".NET 8": "net8", ".NET 9": "net9", ".NET 10": "net10"}
WORKFLOW_PATH = ".github/workflows/bugfix-full-ci.yml"
CHECK_JOBS = {
    "build-and-test / Build (Linux)",
    "build-and-test / Build (macOS)",
    "build-and-test / Build (Windows)",
    "repro-runner / Generate matrix",
}
EXPECTED_TEST_JOBS = 33
MAX_ARCHIVE = 64 * 1024 * 1024
MAX_EXPANDED = 128 * 1024 * 1024
MAX_MEMBERS = 500
REPRO_JOB = re.compile(
    r"repro-runner / Run ([A-Za-z0-9_]+) on (ubuntu-22\.04|ubuntu-24\.04|windows-2022)")


class CollectionError(ValueError):
    """The run or its artifacts cannot provide complete acceptance evidence."""


def require(condition, message):
    if not condition:
        raise CollectionError(message)


def gh(arguments, binary=False):
    result = subprocess.run(["gh", *arguments], check=True, stdout=subprocess.PIPE,
                            stderr=subprocess.PIPE)
    return result.stdout if binary else json.loads(result.stdout)


def api_json(repository, resource):
    return gh(["api", f"repos/{repository}/{resource}"])


def paged(repository, resource, key):
    values = []
    page = 1
    while True:
        data = api_json(repository, f"{resource}?per_page=100&page={page}")
        batch = data.get(key)
        require(isinstance(batch, list), f"GitHub response has no {key} array")
        values.extend(batch)
        if len(batch) < 100:
            require(data.get("total_count") == len(values), f"Incomplete GitHub {key} pagination")
            return values
        page += 1


def artifact_bytes(repository, artifact):
    require(type(artifact.get("id")) is int, "Artifact ID is missing")
    require(type(artifact.get("size_in_bytes")) is int
            and 0 < artifact["size_in_bytes"] <= MAX_ARCHIVE,
            f"Artifact exceeds download bound: {artifact.get('name')}")
    data = gh(["api", f"repos/{repository}/actions/artifacts/{artifact['id']}/zip"], binary=True)
    require(len(data) <= MAX_ARCHIVE,
            f"Downloaded artifact exceeds size bound: {artifact.get('name')}")
    return data


def zip_members(data):
    require(len(data) <= MAX_ARCHIVE, "Artifact ZIP exceeds size bound")
    try:
        with zipfile.ZipFile(io.BytesIO(data)) as archive:
            entries = archive.infolist()
            require(len(entries) <= MAX_MEMBERS, "Artifact ZIP contains too many members")
            require(sum(entry.file_size for entry in entries) <= MAX_EXPANDED,
                    "Expanded artifact ZIP exceeds size bound")
            names = set()
            members = {}
            for entry in entries:
                name = entry.orig_filename
                path = PurePosixPath(name)
                require(not path.is_absolute() and ".." not in path.parts
                        and "\\" not in name and ":" not in name,
                        "Unsafe artifact member path")
                require(name not in names, "Duplicate artifact ZIP member")
                names.add(name)
                require(not stat.S_ISLNK(entry.external_attr >> 16),
                        "Artifact ZIP contains symlink")
                if not entry.is_dir():
                    members[name] = archive.read(entry)
            return members
    except (zipfile.BadZipFile, RuntimeError) as error:
        raise CollectionError("Malformed artifact ZIP") from error


def one_member(members, suffix, artifact_name):
    matches = [value for name, value in members.items() if name.replace("\\", "/").endswith(suffix)]
    require(len(matches) == 1, f"{artifact_name} must contain exactly one {suffix}")
    return matches[0]


def artifact_for_job(job_name):
    prefix = "build-and-test / "
    if not job_name.startswith(prefix):
        return None
    short = job_name[len(prefix):]
    match = re.fullmatch(r"Test \(Linux (\.NET (?:8|9|10))\)", short)
    if match:
        return f"bugfix-full-ci-tests-linux-{SDK_KEYS[match.group(1)]}"
    match = re.fullmatch(r"Test \(Linux ARM64 - (\.NET (?:8|9|10))\)", short)
    if match:
        return f"bugfix-full-ci-tests-linux-arm64-{SDK_KEYS[match.group(1)]}"
    match = re.fullmatch(r"Test \(macOS (\.NET (?:8|9|10))\)", short)
    if match:
        return f"bugfix-full-ci-tests-macos-{SDK_KEYS[match.group(1)]}"
    match = re.fullmatch(
        r"Test \(Windows (windows-(?:latest|2022)) - (x64|x86) - (\.NET (?:8|9|10))\)", short)
    if match:
        return f"bugfix-full-ci-tests-{match.group(1)}-{match.group(2)}-{SDK_KEYS[match.group(3)]}"
    match = re.fullmatch(
        r"Cross-Process Tests \(Windows (windows-(?:latest|2022)) - (x64|x86) - (\.NET (?:8|9|10))\)", short)
    if match:
        return f"bugfix-full-ci-crossprocess-{match.group(1)}-{match.group(2)}-{SDK_KEYS[match.group(3)]}"
    return None


def discovery_names(raw):
    text = raw.decode("utf-8-sig")
    marker = "The following Tests are available:"
    require(text.count(marker) == 1, "Expected one complete test discovery section")
    names = [line.strip() for line in text.split(marker, 1)[1].splitlines() if line.strip()]
    require(names and all(name.startswith("LiteDB.") for name in names),
            "Unexpected test discovery output")
    return names


def claimed_architecture(job_name):
    if "Linux ARM64" in job_name:
        return "arm64"
    match = re.search(r"Windows windows-(?:latest|2022) - (x64|x86) -", job_name)
    return match.group(1) if match else None


def load_trx_module(control_root):
    path = Path(control_root) / "scripts/bugfix/trx.py"
    spec = importlib.util.spec_from_file_location("_full_ci_trx", path)
    require(spec is not None and spec.loader is not None, "Cannot load trusted TRX parser")
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    sys.modules["trx"] = module
    return module


def load_failure_policy(control_root, path):
    module_path = Path(control_root) / "scripts/bugfix/failure_normalization.py"
    spec = importlib.util.spec_from_file_location("_full_ci_failure_normalization", module_path)
    require(spec is not None and spec.loader is not None,
            "Cannot load trusted failure normalizer")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    policy, digest = module.load_failure_normalization(path)
    return module, policy, digest


def test_job(job, data, artifact, trx_module, normalizer, failure_policy, baseline):
    members = zip_members(data)
    inventory = discovery_names(one_member(members, "discovery.txt", artifact["name"]))
    claimed = claimed_architecture(job["name"])
    trx = one_member(members, ".trx", artifact["name"])
    with tempfile.TemporaryDirectory(prefix="litedb-full-ci-trx-") as directory:
        temporary = Path(directory) / "results.trx"
        temporary.write_bytes(trx)
        exit_code = 0 if job["conclusion"] == "success" else 1
        try:
            run = trx_module.read_trx(temporary, exit_code)
        except trx_module.GateError as error:
            raise CollectionError(f"Invalid TRX for {job['name']}: {error}") from error
    require(Counter(run.definitions.values()) == Counter(inventory),
            f"TRX definitions differ from discovery in {job['name']}")
    tests = []
    outcomes = {"Passed": "passed", "Failed": "failed", "NotExecuted": "skipped"}
    for identity, result in sorted(run.tests.items()):
        item = {"identity": identity, "name": result.name, "class_name": result.class_name,
                "outcome": outcomes[result.outcome]}
        if result.outcome == "Failed":
            classification = normalizer.canonical_failure(
                result.name, result.failure, failure_policy, baseline=baseline)
            item.update(failure=result.message, failure_classification=classification)
        tests.append(item)
    return {"name": job["name"], "kind": "tests", "status": job["status"],
            "conclusion": job["conclusion"], "job_id": job["id"], "url": job["html_url"],
            "artifact_id": artifact["id"], "artifact_sha256": hashlib.sha256(data).hexdigest(),
            "runtime_architecture": "unmeasured", "claimed_architecture": claimed,
            "architecture_verified": None if claimed is None else False,
            "tests": tests}


def enum_name(value):
    values = {0: "reproduce", 1: "no_repro", 2: "hard_fail"}
    if isinstance(value, int):
        return values.get(value, f"unknown:{value}")
    return str(value).replace("NoRepro", "no_repro").lower()


def repro_job(job, data, artifact, overlays, source_sha, definition_sha):
    match = REPRO_JOB.fullmatch(job["name"])
    require(match is not None, f"Unexpected repro job name: {job['name']}")
    members = zip_members(data)
    overlay_members = [value for name, value in members.items()
                       if name.endswith("harness-overlay-provenance.json")]
    overlay = None
    repro_name = match.group(1)
    if repro_name in overlays:
        require(len(overlay_members) == 1,
                f"{repro_name} must contain one harness overlay provenance record")
        overlay = json.loads(overlay_members[0])
        overlay_manifest, overlay_manifest_sha, overlay_contract = overlays[repro_name]
        expected_overlay = overlay_contract(
            overlay_manifest, overlay_manifest_sha, source_sha, definition_sha, match.group(2))
        require(overlay == expected_overlay,
                f"{repro_name} harness overlay provenance does not match the trusted definition")
    else:
        require(not overlay_members,
                f"Unexpected harness overlay provenance in {job['name']}")
    console = one_member(members, "repro-console.log", artifact["name"]).decode(
        "utf-8-sig", errors="replace")
    diagnostics = []
    for line in console.splitlines():
        detail = line.strip()
        if detail and (detail.startswith("FAIL:") or "error " in detail.lower()
                       or "variant did not execute" in detail.lower()
                       or detail.lower().startswith("build failed")):
            if detail not in diagnostics:
                diagnostics.append(detail)
    try:
        report = json.loads(one_member(members, "repro-report.json", artifact["name"]))
        entries = report.get("Repros")
        require(isinstance(entries, list) and len(entries) == 1, "Expected one repro report entry")
        entry = entries[0]
        require(entry.get("Id") == match.group(1), "Repro report identity changed")
        failed = entry.get("Failed")
        warned = entry.get("Warned")
        latest = entry.get("Latest")
        package = entry.get("Package")
        require(isinstance(failed, bool) and isinstance(warned, bool)
                and isinstance(latest, dict) and isinstance(package, dict),
                "Incomplete repro report")
        for label, variant in (("package", package), ("latest", latest)):
            reason = variant.get("FailureReason")
            if isinstance(reason, str) and reason.strip():
                detail = f"{label}: {reason.strip()}"
                if detail not in diagnostics:
                    diagnostics.append(detail)
        if failed:
            verdict = "harness_error"
        elif warned:
            verdict = "inconclusive"
        else:
            verdict = "bug_present" if enum_name(latest.get("Expected")) == "reproduce" \
                else "behavior_correct"
        fields = ("Expected", "ExpectedExitCode", "ExpectedLogContains", "Actual", "Met",
                  "ExitCode", "UseProjectReference", "FailureReason")
        classification = json.dumps({
            "state": entry.get("State"), "failed": failed, "warned": warned,
            "package": {field: package.get(field) for field in fields},
            "latest": {field: latest.get(field) for field in fields},
        }, sort_keys=True, separators=(",", ":"))
    except (CollectionError, json.JSONDecodeError, KeyError, TypeError) as error:
        verdict = "harness_error"
        classification = f"unreadable structured repro evidence: {error}"
    result = {"name": job["name"], "kind": "repro", "status": job["status"],
            "conclusion": job["conclusion"], "job_id": job["id"], "url": job["html_url"],
            "artifact_id": artifact["id"], "artifact_sha256": hashlib.sha256(data).hexdigest(),
            "verdict": verdict, "classification": classification,
            "diagnostics": diagnostics[:20]}
    if overlay is not None:
        result["harness_overlay"] = overlay
    return result


def archive_json(directory, name, value):
    (directory / name).write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")


def overlay_summary(manifest, definition_sha):
    return {**{key: copy.deepcopy(value) for key, value in manifest.items()
               if key != "schema_version"},
            "evidence_definition_sha": definition_sha}


def collect(args):
    require(SHA.fullmatch(args.source_sha or ""), "source_sha must be a full lowercase SHA")
    require(SHA.fullmatch(args.evidence_definition_sha or ""),
            "evidence_definition_sha must be a full lowercase SHA")
    quarantine = load_quarantine(args.quarantine)
    overlay_path = Path(args.harness_overlay_manifest)
    issue_2825_overlay_path = Path(args.issue_2825_harness_overlay_manifest)
    if not overlay_path.is_absolute():
        overlay_path = Path(args.control_root) / overlay_path
    if not issue_2825_overlay_path.is_absolute():
        issue_2825_overlay_path = Path(args.control_root) / issue_2825_overlay_path
    overlay_manifest, overlay_manifest_sha = load_issue_2794_harness_overlay(overlay_path)
    issue_2825_overlay_manifest, issue_2825_overlay_manifest_sha = \
        load_issue_2825_harness_overlay(issue_2825_overlay_path)
    overlays = {
        "Issue_2794_SharedJobHandoff": (
            overlay_manifest, overlay_manifest_sha, issue_2794_harness_overlay_contract),
        "Issue_2825_FreeListRace": (
            issue_2825_overlay_manifest, issue_2825_overlay_manifest_sha,
            issue_2825_harness_overlay_contract),
    }
    run = api_json(args.repository, f"actions/runs/{args.run_id}")
    jobs = paged(args.repository, f"actions/runs/{args.run_id}/jobs", "jobs")
    artifacts = paged(args.repository, f"actions/runs/{args.run_id}/artifacts", "artifacts")
    require(run.get("id") == args.run_id and run.get("status") == "completed", "Run is incomplete")
    require(run.get("path") == WORKFLOW_PATH and run.get("event") == "workflow_dispatch",
            "Run did not use the trusted full-CI workflow")
    require(run.get("head_sha") == args.evidence_definition_sha,
            "Run used a different workflow definition commit")
    require(len(jobs) == quarantine["remaining_jobs"], "Full CI job count changed")
    require(len({job.get("name") for job in jobs}) == len(jobs), "Duplicate full CI job names")
    require(all(job.get("status") == "completed" for job in jobs), "A full CI job is incomplete")
    require(not (quarantine["jobs"] & {job["name"] for job in jobs}),
            "A quarantined repro job unexpectedly executed")
    by_artifact = {artifact["name"]: artifact for artifact in artifacts
                   if not artifact.get("expired")}
    require(len(by_artifact) == sum(not artifact.get("expired") for artifact in artifacts),
            "Duplicate artifact names")
    root = Path(args.archive_dir) / str(args.run_id) if args.archive_dir else None
    temporary = None
    if root is None:
        temporary = tempfile.TemporaryDirectory(prefix=f"litedb-full-ci-{args.run_id}-")
        root = Path(temporary.name)
    else:
        root.mkdir(parents=True, exist_ok=False)
    archive_json(root, "run.json", run)
    archive_json(root, "jobs.json", {"total_count": len(jobs), "jobs": jobs})
    archive_json(root, "artifacts.json", {"total_count": len(artifacts), "artifacts": artifacts})
    (root / "quarantine.json").write_bytes(Path(args.quarantine).read_bytes())
    (root / "harness-overlay-manifest.json").write_bytes(overlay_path.read_bytes())
    (root / "issue-2825-harness-overlay-manifest.json").write_bytes(
        issue_2825_overlay_path.read_bytes())
    cache = {}

    def download(name):
        require(name in by_artifact, f"Missing artifact: {name}")
        if name not in cache:
            cache[name] = artifact_bytes(args.repository, by_artifact[name])
            (root / f"{name}.zip").write_bytes(cache[name])
        return cache[name], by_artifact[name]

    provenance_data, _ = download("bugfix-full-ci-provenance")
    provenance = json.loads(one_member(zip_members(provenance_data),
                                       "full-ci-provenance.json", "provenance"))
    expected = {"schema_version": 3, "issue": args.issue, "source_sha": args.source_sha,
                "checkout_sha": args.source_sha,
                "evidence_definition_sha": args.evidence_definition_sha, "run_id": args.run_id}
    expected["quarantine_sha256"] = quarantine["sha256"]
    expected["harness_overlay_manifest_sha256"] = overlay_manifest_sha
    expected["issue_2825_harness_overlay_manifest_sha256"] = \
        issue_2825_overlay_manifest_sha
    require(provenance == expected, "Provenance artifact does not match the requested run")
    trx_module = load_trx_module(args.control_root)
    normalizer, failure_policy, failure_policy_sha = load_failure_policy(
        args.control_root, args.failure_normalization)
    normalized = []
    observed_checks = set()
    test_artifacts = set()
    for job in jobs:
        artifact_name = artifact_for_job(job["name"])
        repro = REPRO_JOB.fullmatch(job["name"])
        if artifact_name:
            test_artifacts.add(artifact_name)
            data, artifact = download(artifact_name)
            normalized.append(test_job(job, data, artifact, trx_module, normalizer,
                                       failure_policy, args.role == "baseline"))
        elif repro:
            data, artifact = download(f"logs-{repro.group(1)}-{repro.group(2)}")
            normalized.append(repro_job(
                job, data, artifact, overlays, args.source_sha,
                args.evidence_definition_sha))
        else:
            require(job["name"] in CHECK_JOBS, f"Unexpected full CI job: {job['name']}")
            observed_checks.add(job["name"])
            normalized.append({"name": job["name"], "kind": "check", "status": job["status"],
                               "conclusion": job["conclusion"], "job_id": job["id"],
                               "url": job["html_url"]})
    require(observed_checks == CHECK_JOBS, "Required full CI checks changed")
    require(len(test_artifacts) == EXPECTED_TEST_JOBS,
            f"Expected all {EXPECTED_TEST_JOBS} original test-matrix artifacts")
    if temporary:
        temporary.cleanup()
    return {"schema_version": 1, "accepted": True, "issue": args.issue,
            "role": args.role,
            "source_sha": args.source_sha,
            "evidence_definition_sha": args.evidence_definition_sha,
            "failure_normalization_sha256": failure_policy_sha,
            "quarantine_sha256": quarantine["sha256"],
            "harness_overlay_manifest_sha256": overlay_manifest_sha,
            "harness_overlay": overlay_summary(
                overlay_manifest, args.evidence_definition_sha),
            "issue_2825_harness_overlay_manifest_sha256":
                issue_2825_overlay_manifest_sha,
            "issue_2825_harness_overlay": overlay_summary(
                issue_2825_overlay_manifest, args.evidence_definition_sha),
            "coverage_gaps": quarantine["coverage_gaps"],
            "run": {"id": run["id"], "head_sha": run["head_sha"], "path": run["path"],
                    "workflow_path": run["path"], "event": run["event"],
                    "status": run["status"], "conclusion": run["conclusion"],
                    "url": run["html_url"]},
            "jobs": normalized}


def parser():
    result = argparse.ArgumentParser(description=__doc__)
    result.add_argument("--repository", required=True)
    result.add_argument("--run-id", required=True, type=int)
    result.add_argument("--issue", required=True, type=int)
    result.add_argument("--source-sha", required=True)
    result.add_argument("--role", required=True, choices=("baseline", "candidate"))
    result.add_argument("--evidence-definition-sha", required=True)
    result.add_argument("--control-root", default=".")
    result.add_argument("--quarantine", default=".github/bugfix/full-ci-quarantine.json")
    result.add_argument("--failure-normalization",
                        default="scripts/bugfix/failure-normalization.json")
    result.add_argument("--harness-overlay-manifest",
                        default=".github/bugfix/issue-2794-harness-overlay.json")
    result.add_argument("--issue-2825-harness-overlay-manifest",
                        default=".github/bugfix/issue-2825-harness-overlay.json")
    result.add_argument("--archive-dir")
    result.add_argument("--output", required=True)
    return result


def main(argv=None):
    args = parser().parse_args(argv)
    try:
        report = collect(args)
    except (CollectionError, OSError, ValueError, KeyError, TypeError,
            subprocess.CalledProcessError) as error:
        report = {"schema_version": 1, "accepted": False, "outcome": "harness_error",
                  "errors": [str(error)]}
    output = Path(args.output)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(json.dumps(report))
    return 0 if report.get("accepted") else 1


if __name__ == "__main__":
    sys.exit(main())
