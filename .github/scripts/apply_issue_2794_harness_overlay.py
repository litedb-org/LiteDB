#!/usr/bin/env python3
"""Apply and attest the reviewed #2794 CI harness timing correction."""

import argparse
import hashlib
import json
from pathlib import Path
import re
import subprocess
import sys


SHA = re.compile(r"[0-9a-f]{40}")
SHA256 = re.compile(r"[0-9a-f]{64}")
MANIFEST_PATH = ".github/bugfix/issue-2794-harness-overlay.json"


class OverlayError(ValueError):
    """The source or trusted overlay does not match its immutable contract."""


def require(condition, message):
    if not condition:
        raise OverlayError(message)


def git(root, *arguments, binary=False):
    result = subprocess.run(
        ["git", "-C", str(root), *arguments], check=True,
        stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    return result.stdout if binary else result.stdout.decode("utf-8").strip()


def load_manifest_path(path):
    path = Path(path)
    raw = path.read_bytes()
    value = json.loads(raw)
    required = {
        "schema_version", "id", "issue", "source_path", "overlay_path",
        "original_git_blob_sha1", "original_blob_sha256",
        "effective_git_blob_sha1", "effective_blob_sha256",
        "dependency_blobs", "wait_timeout_seconds", "ready_timeout_seconds",
        "completion_timeout_seconds", "workload",
    }
    require(isinstance(value, dict) and set(value) == required,
            "Invalid #2794 harness overlay manifest shape")
    require(value["schema_version"] == 1 and value["issue"] == 2794,
            "Invalid #2794 harness overlay manifest identity")
    require(value["id"] == "issue-2794-per-wait-deadline",
            "Unexpected #2794 harness overlay ID")
    require(value["wait_timeout_seconds"] == 60
            and value["ready_timeout_seconds"] == 15
            and value["completion_timeout_seconds"] == 90,
            "Unexpected #2794 timeout contract")
    require(value["workload"] == {"attempts": 3, "rows": 24, "rounds": 24},
            "Unexpected #2794 workload contract")
    dependencies = value["dependency_blobs"]
    require(isinstance(dependencies, dict) and set(dependencies) == {
        "LiteDB.ReproRunner/Repros/Issue_2794_SharedJobHandoff/Program.cs",
        "LiteDB.ReproRunner/Repros/Issue_2794_SharedJobHandoff/Worker/JobLedger.cs",
        "LiteDB.ReproRunner/Repros/Issue_2794_SharedJobHandoff/repro.json",
    }, "Unexpected #2794 harness dependency set")
    for dependency in dependencies.values():
        require(isinstance(dependency, dict)
                and set(dependency) == {"git_blob_sha1", "blob_sha256"}
                and SHA.fullmatch(dependency.get("git_blob_sha1", ""))
                and SHA256.fullmatch(dependency.get("blob_sha256", "")),
                "Invalid #2794 harness dependency hashes")
    require(SHA.fullmatch(value.get("original_git_blob_sha1", ""))
            and SHA256.fullmatch(value.get("original_blob_sha256", ""))
            and SHA.fullmatch(value.get("effective_git_blob_sha1", ""))
            and SHA256.fullmatch(value.get("effective_blob_sha256", "")),
            "Invalid #2794 worker blob hashes")
    return value, hashlib.sha256(raw).hexdigest()


def load_manifest(definition_root):
    return load_manifest_path(Path(definition_root) / MANIFEST_PATH)


def committed_blob(root, revision, path):
    blob_sha = git(root, "rev-parse", f"{revision}:{path}")
    blob = git(root, "show", f"{revision}:{path}", binary=True)
    return blob_sha, hashlib.sha256(blob).hexdigest(), blob


def provenance_contract(manifest, manifest_sha, source_sha, definition_sha, target_os):
    return {
        "schema_version": 1,
        "id": manifest["id"],
        "issue": 2794,
        "source_sha": source_sha,
        "evidence_definition_sha": definition_sha,
        "target_os": target_os,
        "source_path": manifest["source_path"],
        "overlay_path": manifest["overlay_path"],
        "manifest_sha256": manifest_sha,
        "original_git_blob_sha1": manifest["original_git_blob_sha1"],
        "original_blob_sha256": manifest["original_blob_sha256"],
        "effective_git_blob_sha1": manifest["effective_git_blob_sha1"],
        "effective_blob_sha256": manifest["effective_blob_sha256"],
        "dependency_blobs": manifest["dependency_blobs"],
        "wait_timeout_seconds": manifest["wait_timeout_seconds"],
        "ready_timeout_seconds": manifest["ready_timeout_seconds"],
        "completion_timeout_seconds": manifest["completion_timeout_seconds"],
        "workload": manifest["workload"],
    }


def transformed_worker(original):
    replacements = (
        (b"        private static readonly Stopwatch Deadline = Stopwatch.StartNew();\n\n", b""),
        (b"        {\n            while (!condition())\n",
         b"        {\n            var deadline = Stopwatch.StartNew();\n"
         b"            while (!condition())\n"),
        (b"if (Deadline.Elapsed > TimeSpan.FromSeconds(60))",
         b"if (deadline.Elapsed > TimeSpan.FromSeconds(60))"),
    )
    effective = original
    for before, after in replacements:
        require(effective.count(before) == 1,
                "Frozen #2794 worker does not contain the reviewed unique timing edit")
        effective = effective.replace(before, after, 1)
    return effective


def apply_overlay(source_root, definition_root, source_sha, definition_sha,
                  target_os, output_path):
    require(SHA.fullmatch(source_sha or ""), "source SHA must be a full lowercase SHA")
    require(SHA.fullmatch(definition_sha or ""),
            "definition SHA must be a full lowercase SHA")
    require(target_os in ("ubuntu-22.04", "ubuntu-24.04", "windows-2022"),
            "Unexpected #2794 target OS")
    require(git(source_root, "rev-parse", "HEAD") == source_sha,
            "Source checkout does not match the requested SHA")
    require(git(definition_root, "rev-parse", "HEAD") == definition_sha,
            "Overlay checkout does not match the evidence definition SHA")
    require(not git(source_root, "status", "--porcelain", "--untracked-files=no"),
            "Source checkout already has tracked changes")

    manifest, manifest_sha = load_manifest(definition_root)
    source_path = manifest["source_path"]
    overlay_path = manifest["overlay_path"]
    original_sha1, original_sha256, original_blob = committed_blob(
        source_root, source_sha, source_path)
    effective_sha1, effective_sha256, effective_blob = committed_blob(
        definition_root, definition_sha, overlay_path)
    require(original_sha1 == manifest["original_git_blob_sha1"]
            and original_sha256 == manifest["original_blob_sha256"],
            "Frozen #2794 worker blob differs from the reviewed original")
    require(effective_sha1 == manifest["effective_git_blob_sha1"]
            and effective_sha256 == manifest["effective_blob_sha256"],
            "Trusted #2794 overlay blob differs from its manifest")
    require(transformed_worker(original_blob) == effective_blob,
            "Trusted #2794 overlay contains changes beyond the reviewed timing edit")
    require(git(source_root, "hash-object", source_path) == original_sha1,
            "Working #2794 worker does not match the committed source blob")
    for dependency_path, expected in manifest["dependency_blobs"].items():
        dependency_sha1, dependency_sha256, _ = committed_blob(
            source_root, source_sha, dependency_path)
        require(expected == {"git_blob_sha1": dependency_sha1,
                             "blob_sha256": dependency_sha256},
                f"Frozen #2794 harness dependency changed: {dependency_path}")
        require(git(source_root, "hash-object", dependency_path) == dependency_sha1,
                f"Working #2794 harness dependency changed: {dependency_path}")

    target = Path(source_root) / source_path
    target.write_bytes(effective_blob)
    require(git(source_root, "hash-object", source_path) == effective_sha1,
            "Applied #2794 worker does not match the trusted overlay blob")
    changed = git(source_root, "diff", "--name-only").splitlines()
    require(changed == [source_path], "Harness overlay changed an unexpected tracked file")

    provenance = provenance_contract(
        manifest, manifest_sha, source_sha, definition_sha, target_os)
    output = Path(output_path)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(provenance, indent=2) + "\n", encoding="utf-8")
    return provenance


def parser():
    result = argparse.ArgumentParser(description=__doc__)
    result.add_argument("--source-root", required=True)
    result.add_argument("--definition-root", required=True)
    result.add_argument("--source-sha", required=True)
    result.add_argument("--definition-sha", required=True)
    result.add_argument("--target-os", required=True)
    result.add_argument("--output", required=True)
    return result


def main(argv=None):
    args = parser().parse_args(argv)
    try:
        apply_overlay(args.source_root, args.definition_root, args.source_sha,
                      args.definition_sha, args.target_os, args.output)
    except (OverlayError, OSError, ValueError, KeyError, TypeError,
            subprocess.CalledProcessError) as error:
        print(f"#2794 harness overlay rejected: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
