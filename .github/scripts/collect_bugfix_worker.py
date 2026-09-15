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
CODEX_VERSION = "0.154.0"


def runtime_evidence(env: dict, required_model: str) -> tuple[dict, bytes]:
    control = Path(env.get("RUNNER_TEMP", "/tmp")) / "gh-aw" / "bugfix-control"
    proof_path = Path(env.get("BUGFIX_RUNTIME_PROOF", str(control / "runtime-proof.json")))
    proof_bytes = proof_path.read_bytes()
    require(len(proof_bytes) <= 16384, "Oversized runtime proof")
    proof = json.loads(proof_bytes)
    require(set(proof) == {"schema_version", "codex_version", "transport", "models"}, "Invalid runtime proof fields")
    require(type(proof["schema_version"]) is int and proof["schema_version"] == 1, "Invalid runtime proof schema")
    require(proof["codex_version"] == CODEX_VERSION and proof["transport"] == "local_stub", "Wrong runtime proof version or transport")
    models = proof["models"]
    require(isinstance(models, list) and len(models) == 2, "Runtime proof must cover both worker models")
    for record in models:
        require(isinstance(record, dict) and set(record) == {"model", "reasoning_effort", "requests_checked"}, "Invalid runtime request evidence")
        require(record["reasoning_effort"] == "high", "Runtime proof lacks high reasoning")
        require(type(record["requests_checked"]) is int and record["requests_checked"] > 0, "Runtime proof captured no requests")
    require({record["model"] for record in models} == {"gpt-6-astra", "gpt-5.6-sol"}, "Runtime proof model set differs")

    usage_path = Path(env.get("BUGFIX_AGENT_USAGE", "/tmp/gh-aw/agent_usage.json"))
    usage = json.loads(usage_path.read_text(encoding="utf-8"))
    require(usage.get("primary_model") == required_model, "Runtime primary model differs from required worker model")
    reported_effort = usage.get("reasoning_effort")
    require(not reported_effort or reported_effort == "high", "Runtime reasoning differs from required high effort")
    token_path = Path(env.get("BUGFIX_TOKEN_USAGE", "/tmp/gh-aw/sandbox/firewall/logs/api-proxy-logs/token-usage.jsonl"))
    records = [json.loads(line) for line in token_path.read_text(encoding="utf-8").splitlines() if line.strip()]
    requests = [record for record in records if record.get("event") == "token_usage"]
    require(bool(requests), "No actual runtime model request evidence")
    require(all(record.get("model") == required_model for record in requests), "Runtime invoked a different model")
    metadata = {
        "runtime_proof_sha256": hashlib.sha256(proof_bytes).hexdigest(),
        "codex_version": CODEX_VERSION,
        "verified_reasoning_effort": "high",
        "reasoning_verification": "local-request-capture",
        "reported_model": usage["primary_model"],
        "reported_reasoning_effort": reported_effort,
        "observed_request_models": [required_model],
        "observed_request_count": len(requests),
        "accounted_ai_credits": max((record.get("ai_credits_total", 0) for record in requests), default=0),
    }
    return metadata, proof_bytes


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
    require(not (output / "runtime-proof.json").exists() and not (output / "runtime-proof.json").is_symlink(), "Worker must not supply the trusted runtime proof")
    result = json.loads(result_path.read_text(encoding="utf-8"))
    validate_result(result, expected)
    required_model = "gpt-5.6-sol" if "role" in expected else "gpt-6-astra"
    require(env.get("BUGFIX_MODEL") == required_model, "Unexpected worker model")
    require(env.get("BUGFIX_REASONING_EFFORT") == "high", "Worker reasoning must be high")
    runtime, proof_bytes = runtime_evidence(env, required_model)
    changed = git(repo, "diff", "--name-status", "--no-renames", "HEAD", "--").decode().splitlines()
    metadata = {**expected, **runtime, "kind": "review" if "role" in expected else "fix"}
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
    (output / "runtime-proof.json").write_bytes(proof_bytes)
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
