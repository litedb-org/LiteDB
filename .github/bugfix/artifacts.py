"""Read bounded Actions archives and validate their actual acceptance payloads."""

import hashlib
import io
import json
from pathlib import PurePosixPath
import stat
import subprocess
import zipfile

from state import Rejected, require

MAX_ARCHIVE = 64 * 1024 * 1024
MAX_EXPANDED = 128 * 1024 * 1024
MAX_REPORT = 256 * 1024
HASH_FIELDS = ("artifact_sha256", "report_sha256")


def validate_worker_model(metadata, role):
    expected = "gpt-6-astra" if role == "fix" else "gpt-5.6-sol"
    require(metadata.get("configured_model") == expected, "Worker configured model mismatch")
    require(metadata.get("configured_reasoning_effort") == "high", "Worker must use high reasoning effort")
    require(metadata.get("reported_model") in (None, "", expected), "Worker reported a different model")
    require(metadata.get("reported_reasoning_effort") in (None, "", "high"), "Worker reported different reasoning effort")


def download(repo, artifact):
    require(type(artifact.get("id")) is int, "Artifact ID missing")
    require(type(artifact.get("size_in_bytes")) is int and
            0 < artifact["size_in_bytes"] <= MAX_ARCHIVE, "Artifact exceeds download bound")
    result = subprocess.run(["gh", "api", f"repos/{repo}/actions/artifacts/{artifact['id']}/zip"],
                            capture_output=True, check=False)
    require(result.returncode == 0, "Cannot download evidence archive")
    require(len(result.stdout) <= MAX_ARCHIVE, "Downloaded archive exceeds size bound")
    return result.stdout


def _reports(data):
    """Inspect members without extracting any archive-controlled filesystem path."""
    try:
        archive = zipfile.ZipFile(io.BytesIO(data))
        members = archive.infolist()
        require(len(members) <= 500, "Archive contains too many files")
        require(sum(member.file_size for member in members) <= MAX_EXPANDED, "Expanded archive exceeds bound")
        names = set()
        reports = {}
        for member in members:
            name = member.orig_filename
            path = PurePosixPath(name)
            require(not path.is_absolute() and ".." not in path.parts and "\\" not in name and ":" not in name,
                    "Unsafe evidence archive path")
            require(name not in names, "Duplicate evidence archive member")
            names.add(name)
            require(not stat.S_ISLNK(member.external_attr >> 16), "Evidence archive contains symlink")
            if name in ("verdict.json", "scope.json", "result.json", "metadata.json"):
                require(member.file_size <= MAX_REPORT, "Evidence report exceeds size bound")
                raw = archive.read(member)
                report = json.loads(raw)
                require(isinstance(report, dict), "Evidence report must be an object")
                reports[name] = (report, hashlib.sha256(raw).hexdigest())
        archive.close()
        return reports
    except (zipfile.BadZipFile, UnicodeDecodeError, json.JSONDecodeError, RuntimeError) as error:
        raise Rejected("Invalid evidence archive or JSON report") from error


def _matching(report, expected, fields):
    for field in fields:
        require(field in report and type(report[field]) is type(expected[field]) and
                report[field] == expected[field], f"Artifact identity mismatch: {field}")


def read_members(data, names):
    """Read a fixed caller-selected subset after the same bounded archive checks."""
    _reports(data)
    with zipfile.ZipFile(io.BytesIO(data)) as archive:
        available = {member.orig_filename: member for member in archive.infolist()}
        return {name: archive.read(available[name]) for name in names if name in available}


def validate(data, event):
    reports = _reports(data)
    if event["kind"] == "review":
        require("result.json" in reports and "metadata.json" in reports, "Missing review result or metadata")
        report, report_hash = reports["result.json"]
        expected = {**event, "schema_version": 1}
        fields = ("schema_version", "issue", "base_sha", "candidate_sha", "test_source_sha", "role")
        _matching(report, expected, fields)
        require(report.get("verdict") == "pass" and report.get("findings") == [],
                "Review payload does not approve the candidate")
        require(event.get("findings") == report["findings"], "Event findings differ from review payload")
        coverage = report.get("coverage")
        require(isinstance(coverage, list) and coverage and
                all(isinstance(item, str) and item.strip() for item in coverage), "Missing concrete review coverage")
        metadata = reports["metadata.json"][0]
        validate_worker_model(metadata, event["role"])
        _matching(metadata, expected, fields + ("workflow_sha",))
        require(metadata.get("kind") == "review", "Unexpected worker artifact kind")
        require(metadata.get("run_id") == str(event["run_id"]), "Review metadata run mismatch")
        require(metadata.get("result_sha256") == report_hash, "Review metadata digest mismatch")
    else:
        require("verdict.json" in reports, "Missing CI verdict payload")
        report, report_hash = reports["verdict.json"]
        expected = {**event, "schema_version": 1, "level": event["kind"]}
        _matching(report, expected, ("schema_version", "issue", "base_sha", "candidate_sha",
                                     "test_source_sha", "workflow_sha", "environment", "level"))
        require(report.get("accepted") is True, "CI payload did not accept the candidate")
        expected_outcome = "bug_present" if event["kind"] == "baseline" else "behavior_correct"
        require(report.get("outcome") == expected_outcome, "CI payload outcome mismatch")
        if event["kind"] == "baseline":
            require(report["candidate_sha"] is None, "Baseline must not grade a candidate")
        else:
            require("scope.json" in reports, "Missing candidate scope protection evidence")
            scope = reports["scope.json"][0]
            require(scope.get("accepted") is True and scope.get("outcome") == "scope_verified",
                    "Candidate scope protection did not pass")
            require(isinstance(scope.get("provenance"), dict), "Missing scope provenance")
            _matching(scope["provenance"], event, ("issue", "base_sha", "candidate_sha"))
    return {"artifact_sha256": hashlib.sha256(data).hexdigest(), "report_sha256": report_hash}
