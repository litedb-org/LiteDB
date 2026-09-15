#!/usr/bin/env python3
"""Validate immutable worker identity and collect a restricted patch or review."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import subprocess
from pathlib import Path


ROLES = {"behavior", "compatibility", "lifecycle"}


def git(repo: Path, *args: str) -> bytes:
    return subprocess.check_output(["git", "-C", str(repo), *args])


def require(condition: bool, message: str) -> None:
    if not condition:
        raise ValueError(message)


def sha(value: str, label: str) -> str:
    require(bool(re.fullmatch(r"[0-9a-f]{40}", value)), f"Invalid {label}")
    return value


def identity(env: dict) -> dict:
    issue = env.get("BUGFIX_ISSUE", "")
    require(bool(re.fullmatch(r"[1-9][0-9]*", issue)), "Invalid issue")
    data = {
        "schema_version": 1,
        "issue": int(issue),
        "base_sha": sha(env.get("BUGFIX_BASE_SHA", ""), "base SHA"),
        "test_source_sha": sha(env.get("BUGFIX_TEST_SOURCE_SHA", ""), "test source SHA"),
    }
    role = env.get("BUGFIX_ROLE", "fix")
    require(role == "fix" or role in ROLES, "Invalid review role")
    if role != "fix":
        data["candidate_sha"] = sha(env.get("BUGFIX_CANDIDATE_SHA", ""), "candidate SHA")
        data["role"] = role
    source_run = env.get("BUGFIX_SOURCE_RUN", "")
    require(not source_run or bool(re.fullmatch(r"[1-9][0-9]*", source_run)), "Invalid source run")
    return data


def validate_contract(repo: Path, manifest: dict, expected: dict) -> dict:
    require(manifest.get("schema_version") == 1, "Unsupported issue manifest")
    contract = manifest.get("issues", {}).get(str(expected["issue"]))
    require(isinstance(contract, dict), "Issue is not eligible for a worker")
    require(contract["frozen_test_revision"] == expected["test_source_sha"], "Test source differs from frozen contract")
    current = git(repo, "rev-parse", "HEAD").decode().strip()
    require(current == expected.get("candidate_sha", expected["base_sha"]), "Worker changed HEAD or checked out the wrong source")
    for path, blob in contract["frozen_test_blobs"].items():
        actual = git(repo, "hash-object", "--", path).decode().strip()
        require(actual == blob, f"Frozen regression changed: {path}")
    require(not git(repo, "ls-files", "--others", "--exclude-standard").strip(), "Worker created untracked repository files")
    return contract


def text_list(value: object, label: str) -> None:
    require(isinstance(value, list) and bool(value), f"{label} must be a nonempty list")
    require(all(isinstance(item, str) and item.strip() for item in value), f"{label} must contain concrete descriptions")


def validate_result(result: dict, expected: dict) -> None:
    require(isinstance(result, dict), "Worker result must be an object")
    for key, value in expected.items():
        require(type(result.get(key)) is type(value) and result.get(key) == value, f"Worker result identity mismatch: {key}")
    if "role" not in expected:
        allowed = set(expected) | {"status", "summary", "tests"}
        require(set(result) == allowed, "Unexpected fix result fields")
        require(result["status"] == "proposed", "Worker did not propose a fix")
        require(isinstance(result["summary"], str) and bool(result["summary"].strip()), "Missing fix summary")
        text_list(result["tests"], "tests")
        return

    require(set(result) == set(expected) | {"verdict", "findings", "coverage"}, "Unexpected review result fields")
    require(result["verdict"] in {"pass", "changes_requested", "inconclusive"}, "Invalid review verdict")
    findings = result["findings"]
    require(isinstance(findings, list), "Findings must be a list")
    require((result["verdict"] == "pass") == (len(findings) == 0), "Verdict and findings disagree")
    for finding in findings:
        require(isinstance(finding, dict) and set(finding) == {"summary", "path", "evidence"}, "Each finding needs summary, path, and evidence")
        require(all(isinstance(value, str) and value.strip() for value in finding.values()), "Finding evidence cannot be empty")
    text_list(result["coverage"], "coverage")


def collect(repo: Path, manifest_path: Path, output: Path, env: dict) -> dict:
    expected = identity(env)
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    contract = validate_contract(repo, manifest, expected)
    result_path = output / "result.json"
    require(not output.is_symlink() and not result_path.is_symlink(), "Evidence cannot use symbolic links")
    require(result_path.is_file() and result_path.stat().st_size <= 262144, "Missing or oversized worker result")
    require(not (output / "metadata.json").exists() and not (output / "metadata.json").is_symlink(), "Stale metadata must not be reused")
    result = json.loads(result_path.read_text(encoding="utf-8"))
    validate_result(result, expected)
    changed = git(repo, "diff", "--name-status", "--no-renames", "HEAD", "--").decode().splitlines()
    metadata = {**expected, "kind": "review" if "role" in expected else "fix"}
    if "role" in expected:
        require(not changed, "Review worker changed tracked files")
    else:
        require(bool(changed), "Fix worker produced an empty patch")
        require(not (output / "patch.diff").exists() and not (output / "patch.diff").is_symlink(), "Worker must leave patch creation to the collector")
        allowed = contract["allowed_production_paths"]
        for line in changed:
            status, path = line.split("\t", 1)
            require(status == "M" and path in allowed, "Patch changes a forbidden path or file type")
            require(not (repo / path).is_symlink(), "Patch changes a symbolic link")
        for change in git(repo, "diff", "--raw", "--no-renames", "HEAD", "--").decode().splitlines():
            modes = change.split()[:2]
            require(modes == [":100644", "100644"], "Patch changes file permissions or a non-regular source file")
        patch = git(repo, "diff", "--binary", "--full-index", "--no-ext-diff", "HEAD", "--")
        require(len(patch) <= 1048576, "Worker patch exceeds the 1 MiB limit")
        (output / "patch.diff").write_bytes(patch)
        metadata["patch_sha256"] = hashlib.sha256(patch).hexdigest()
        metadata["changed_paths"] = [line.split("\t", 1)[1] for line in changed]
    metadata["result_sha256"] = hashlib.sha256(result_path.read_bytes()).hexdigest()
    metadata["workflow_sha"] = sha(env.get("GITHUB_WORKFLOW_SHA", ""), "workflow SHA")
    metadata["run_id"] = env.get("GITHUB_RUN_ID", "")
    metadata["run_attempt"] = env.get("GITHUB_RUN_ATTEMPT", "")
    metadata["configured_model"] = env.get("BUGFIX_MODEL", "")
    metadata["configured_reasoning_effort"] = env.get("BUGFIX_REASONING_EFFORT", "")
    required_model = "gpt-5.6-sol" if "role" in expected else "gpt-6-astra"
    require(metadata["configured_model"] == required_model, "Unexpected worker model")
    require(metadata["configured_reasoning_effort"] == "high", "Worker reasoning must be high")
    usage_path = Path("/tmp/gh-aw/agent_usage.json")
    if usage_path.is_file():
        usage = json.loads(usage_path.read_text(encoding="utf-8"))
        if isinstance(usage, dict):
            metadata["reported_model"] = usage.get("model")
            metadata["reported_reasoning_effort"] = usage.get("reasoning_effort")
            require(not metadata["reported_model"] or metadata["reported_model"] == required_model,
                    "Runtime model differs from required worker model")
            require(not metadata["reported_reasoning_effort"] or metadata["reported_reasoning_effort"] == "high",
                    "Runtime reasoning differs from required high effort")
    (output / "metadata.json").write_text(json.dumps(metadata, indent=2) + "\n", encoding="utf-8")
    return metadata


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--repo", type=Path, default=Path.cwd())
    parser.add_argument("--output", type=Path, default=Path("/tmp/gh-aw/bugfix"))
    args = parser.parse_args()
    try:
        metadata = collect(args.repo, args.manifest, args.output, dict(os.environ))
    except (ValueError, KeyError, OSError, subprocess.CalledProcessError) as error:
        raise SystemExit(f"Worker evidence rejected: {error}") from error
    print(f"Validated {metadata['kind']} evidence for issue {metadata['issue']}")


if __name__ == "__main__":
    main()
