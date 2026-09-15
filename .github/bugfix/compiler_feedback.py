"""Classify bounded compiler diagnostics without treating infrastructure as a code fix."""

import hashlib
import json
from pathlib import PurePosixPath
import re

MAX_LOG = 8 * 1024 * 1024
MAX_DIAGNOSTICS = 20
DIAGNOSTIC = re.compile(r"^\s*(?:\d+>)?(?P<path>.+?\.cs)\((?P<line>\d+),(?P<column>\d+)(?:,\d+,\d+)?\): "
                        r"error (?P<code>CS\d{4}): (?P<message>.+)$")
ERROR = re.compile(r"\berror\s+(?:[A-Z]+\d+\s*:|:)", re.IGNORECASE)
# Missing references/compiler components are not sufficient evidence of a source defect.
DEPENDENCY_CODES = {"CS0006", "CS0012", "CS0518", "CS1069", "CS1705", "CS8032", "CS8785"}


def relative_source(path, repository):
    path, root = path.replace("\\", "/"), repository.replace("\\", "/").rstrip("/")
    prefix = root + "/"
    if path.startswith(prefix):
        path = path[len(prefix):]
    pure = PurePosixPath(path)
    if pure.is_absolute() or ".." in pure.parts or ":" in path or not path.startswith("LiteDB/"):
        return None
    return path


def classify(raw, repository, returncode, timed_out=False):
    """Only complete exit-1 builds with exclusively located C# errors are actionable."""
    result = {"outcome": "harness_error", "diagnostics": []}
    if timed_out or type(returncode) is not int or returncode != 1 or len(raw) > MAX_LOG:
        return result
    diagnostics = []
    for line in raw.decode("utf-8-sig", errors="replace").splitlines():
        if not ERROR.search(line):
            continue
        match = DIAGNOSTIC.fullmatch(line)
        if match is None or match["code"] in DEPENDENCY_CODES:
            return result
        source = relative_source(match["path"], repository)
        if source is None:
            return result
        message = re.sub(r" \[[^\r\n]+\.csproj(?:[^\r\n]*)\]$", "", match["message"])
        item = {"path": source, "line": int(match["line"]), "column": int(match["column"]),
                "code": match["code"], "message": message[:2000]}
        if item not in diagnostics:
            diagnostics.append(item)
        if len(diagnostics) > MAX_DIAGNOSTICS:
            return result
    if diagnostics:
        return {"outcome": "compiler_error", "diagnostics": diagnostics}
    return result


def write_build_report(output, log, repository, identity, returncode, timed_out=False):
    raw = log.read_bytes() if log.is_file() and log.stat().st_size <= MAX_LOG else b""
    outcome = classify(raw, str(repository), returncode, timed_out)
    if returncode == 0 and not timed_out:
        outcome = {"outcome": "success", "diagnostics": []}
    report = {"schema_version": 1, **identity, "repository_root": str(repository),
              "returncode": returncode, "timed_out": timed_out,
              "log_sha256": hashlib.sha256(raw).hexdigest() if raw else None, **outcome}
    output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    return report


def verified_failure(raw_report, raw_log, state, build_kind, framework):
    """Replay parsing and bind changed source, tests, profile and check definition."""
    from state import require
    require(len(raw_report) <= 65536 and len(raw_log) <= MAX_LOG, "Compiler evidence exceeds bounds")
    report = json.loads(raw_report)
    expected = {"schema_version": 1, "issue": state["issue"], "source_sha": state["candidate_sha"],
                "test_source_sha": state["test_source_sha"], "build_kind": build_kind, "framework": framework,
                "workflow_sha": state.get("check_definition", {}).get("workflow_sha", state["workflow_sha"])}
    for field, value in expected.items():
        require(type(report.get(field)) is type(value) and report[field] == value, f"Compiler evidence identity mismatch: {field}")
    require(isinstance(report.get("repository_root"), str) and report["repository_root"], "Missing compiler source root")
    require(type(report.get("timed_out")) is bool, "Missing compiler timeout status")
    require(report.get("log_sha256") == hashlib.sha256(raw_log).hexdigest(), "Compiler log digest changed")
    parsed = classify(raw_log, report["repository_root"], report.get("returncode"), report["timed_out"])
    require(report.get("outcome") == parsed["outcome"] and report.get("diagnostics") == parsed["diagnostics"],
            "Compiler diagnostics differ from raw build log")
    require(parsed["outcome"] == "compiler_error", "Build did not prove an actionable compiler error")
    paths = state.get("acceptance_profile", {}).get("paths", [])
    if build_kind == "test":
        require(framework in {lane["framework"] for lane in state.get("acceptance_profile", {}).get("matrix", [])},
                "Compiler framework is outside the selected profile")
    require(paths and all(item["path"] in paths for item in parsed["diagnostics"]),
            "Compiler errors are outside the changed approved production files")
    return [{"kind": "compiler_error", "build_kind": build_kind, "framework": framework, **item}
            for item in parsed["diagnostics"]]
