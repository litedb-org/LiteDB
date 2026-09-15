#!/usr/bin/env python3
"""Apply and attest the reviewed #2825 repro failure-classifier correction."""

import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import sys

from apply_issue_2794_harness_overlay import (
    OverlayError,
    SHA,
    SHA256,
    committed_blob,
    git,
    require,
)


MANIFEST_PATH = ".github/bugfix/issue-2825-harness-overlay.json"


def load_manifest_path(path):
    path = Path(path)
    raw = path.read_bytes()
    value = json.loads(raw)
    required = {
        "schema_version", "id", "issue", "source_path", "overlay_path",
        "original_git_blob_sha1", "original_blob_sha256",
        "effective_git_blob_sha1", "effective_blob_sha256",
        "dependency_blobs", "primary_failure", "secondary_failure", "workload",
    }
    require(isinstance(value, dict) and set(value) == required,
            "Invalid #2825 harness overlay manifest shape")
    require(value["schema_version"] == 1 and value["issue"] == 2825,
            "Invalid #2825 harness overlay manifest identity")
    require(value["id"] == "issue-2825-data-insert-secondary-failure",
            "Unexpected #2825 harness overlay ID")
    require(value["primary_failure"] == {
        "error_code": "INVALID_DATAFILE_STATE",
        "message": "empty page must be defined as empty type",
        "any_frame": [
            "LiteDB.Engine.Snapshot.NewPage",
            "LiteDB.Engine.EngineState.Validate",
            "LiteDB.Engine.LiteEngine.Insert",
        ],
    }, "Unexpected #2825 primary-failure contract")
    require(value["secondary_failure"] == {
        "error_code": "INVALID_DATAFILE_STATE",
        "message": "page must be writable to support changes",
        "adjacent_frames": [
            "at LiteDB.Engine.BasePage.InternalInsert(",
            "at LiteDB.Engine.BasePage.Insert(",
            "at LiteDB.Engine.DataPage.InsertBlock(",
            "at LiteDB.Engine.DataService.<>c__DisplayClass4_0.<<Insert>g__source|0>d.MoveNext(",
            "at LiteDB.Engine.BufferWriter.MoveForward(",
            "at LiteDB.Engine.BufferWriter.Write(",
            "at LiteDB.Engine.BufferWriter.WriteString(",
            "at LiteDB.Engine.BufferWriter.WriteElement(",
            "at LiteDB.Engine.BufferWriter.WriteDocument(",
            "at LiteDB.Engine.DataService.Insert(",
            "at LiteDB.Engine.LiteEngine.InsertDocument(",
        ],
    }, "Unexpected #2825 secondary-failure contract")
    require(value["workload"] == {"attempts": 3, "workers": 4,
                                   "rounds": 30, "rows": 12},
            "Unexpected #2825 workload contract")
    dependencies = value["dependency_blobs"]
    require(isinstance(dependencies, dict) and set(dependencies) == {
        "LiteDB.ReproRunner/Repros/Issue_2825_FreeListRace/Program.cs",
        "LiteDB.ReproRunner/Repros/Issue_2825_FreeListRace/repro.json",
        "LiteDB.Tests/Issues/Issue2825_Tests.cs",
    }, "Unexpected #2825 harness dependency set")
    for dependency in dependencies.values():
        require(isinstance(dependency, dict)
                and set(dependency) == {"git_blob_sha1", "blob_sha256"}
                and SHA.fullmatch(dependency.get("git_blob_sha1", ""))
                and SHA256.fullmatch(dependency.get("blob_sha256", "")),
                "Invalid #2825 harness dependency hashes")
    require(SHA.fullmatch(value.get("original_git_blob_sha1", ""))
            and SHA256.fullmatch(value.get("original_blob_sha256", ""))
            and SHA.fullmatch(value.get("effective_git_blob_sha1", ""))
            and SHA256.fullmatch(value.get("effective_blob_sha256", "")),
            "Invalid #2825 classifier blob hashes")
    return value, hashlib.sha256(raw).hexdigest()


def load_manifest(definition_root):
    return load_manifest_path(Path(definition_root) / MANIFEST_PATH)


def provenance_contract(manifest, manifest_sha, source_sha, definition_sha, target_os):
    return {
        "schema_version": 1,
        "id": manifest["id"],
        "issue": 2825,
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
        "primary_failure": manifest["primary_failure"],
        "secondary_failure": manifest["secondary_failure"],
        "workload": manifest["workload"],
    }


def transformed_classifier(original):
    before = (
        b'             (error.StackTrace?.Contains("LiteDB.Engine.BasePage.InternalInsert") == true &&\n'
        b'                error.StackTrace.Contains("LiteDB.Engine.IndexService.AddNode")) ||\n'
    )
    after = (
        b'             (error.StackTrace?.Contains("LiteDB.Engine.BasePage.InternalInsert") == true &&\n'
        b'                (error.StackTrace.Contains("LiteDB.Engine.IndexService.AddNode") ||\n'
        b'                 ContainsAdjacentFrames(error.StackTrace,\n'
        b'                    "at LiteDB.Engine.BasePage.InternalInsert(",\n'
        b'                    "at LiteDB.Engine.BasePage.Insert(",\n'
        b'                    "at LiteDB.Engine.DataPage.InsertBlock(",\n'
        b'                    "at LiteDB.Engine.DataService.<>c__DisplayClass4_0.<<Insert>g__source|0>d.MoveNext(",\n'
        b'                    "at LiteDB.Engine.BufferWriter.MoveForward(",\n'
        b'                    "at LiteDB.Engine.BufferWriter.Write(",\n'
        b'                    "at LiteDB.Engine.BufferWriter.WriteString(",\n'
        b'                    "at LiteDB.Engine.BufferWriter.WriteElement(",\n'
        b'                    "at LiteDB.Engine.BufferWriter.WriteDocument(",\n'
        b'                    "at LiteDB.Engine.DataService.Insert(",\n'
        b'                    "at LiteDB.Engine.LiteEngine.InsertDocument("))) ||\n'
    )
    helper_anchor = b"    private static bool IsClosedFileTransactionFailure(Exception error)\n"
    helper = (
        b"    private static bool ContainsAdjacentFrames(string stackTrace, params string[] frames)\n"
        b"    {\n"
        b"        var lines = stackTrace.Replace(\"\\r\\n\", \"\\n\").Split('\\n');\n"
        b"        for (var start = 0; start <= lines.Length - frames.Length; start++)\n"
        b"        {\n"
        b"            if (frames.Select((frame, index) => lines[start + index].TrimStart().StartsWith(\n"
        b"                frame, StringComparison.Ordinal)).All(matches => matches)) return true;\n"
        b"        }\n"
        b"        return false;\n"
        b"    }\n\n"
    )
    require(original.count(before) == 1,
            "Frozen #2825 classifier does not contain the reviewed write branch")
    require(original.count(helper_anchor) == 1,
            "Frozen #2825 classifier does not contain the reviewed helper anchor")
    return original.replace(before, after, 1).replace(helper_anchor, helper + helper_anchor, 1)


def apply_overlay(source_root, definition_root, source_sha, definition_sha,
                  target_os, output_path):
    require(SHA.fullmatch(source_sha or ""), "source SHA must be a full lowercase SHA")
    require(SHA.fullmatch(definition_sha or ""),
            "definition SHA must be a full lowercase SHA")
    require(target_os in ("ubuntu-22.04", "ubuntu-24.04", "windows-2022"),
            "Unexpected #2825 target OS")
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
            "Frozen #2825 classifier blob differs from the reviewed original")
    require(effective_sha1 == manifest["effective_git_blob_sha1"]
            and effective_sha256 == manifest["effective_blob_sha256"],
            "Trusted #2825 overlay blob differs from its manifest")
    require(transformed_classifier(original_blob) == effective_blob,
            "Trusted #2825 overlay contains changes beyond the reviewed classifier edit")
    require(git(source_root, "hash-object", source_path) == original_sha1,
            "Working #2825 classifier does not match the committed source blob")
    for dependency_path, expected in manifest["dependency_blobs"].items():
        dependency_sha1, dependency_sha256, _ = committed_blob(
            source_root, source_sha, dependency_path)
        require(expected == {"git_blob_sha1": dependency_sha1,
                             "blob_sha256": dependency_sha256},
                f"Frozen #2825 harness dependency changed: {dependency_path}")
        require(git(source_root, "hash-object", dependency_path) == dependency_sha1,
                f"Working #2825 harness dependency changed: {dependency_path}")

    target = Path(source_root) / source_path
    target.write_bytes(effective_blob)
    require(git(source_root, "hash-object", source_path) == effective_sha1,
            "Applied #2825 classifier does not match the trusted overlay blob")
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
        print(f"#2825 harness overlay rejected: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
