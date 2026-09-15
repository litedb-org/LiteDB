#!/usr/bin/env python3
"""Compile gh-aw workflows and apply repository-specific lockfile patches."""

from __future__ import annotations

import subprocess
import sys
from pathlib import Path


REPO_ROOT = Path(__file__).resolve().parents[2]
WORKFLOW_DIR = REPO_ROOT / ".github" / "workflows"
COMPILER_VERSION = "v0.82.0-jk.1"
RUNTIME_COMMIT = "21e402d7a4b5367258a7692cbb84fee60b507598"


def selected_lockfiles(args: list[str]) -> list[Path]:
    if "--no-emit" in args:
        return []

    selected: list[Path] = []
    skip_next = False
    for arg in args:
        if skip_next:
            skip_next = False
            continue
        if arg in {
            "--dir", "-d", "--engine", "-e", "--logical-repo", "-l",
            "--schedule-seed", "--action-tag", "--action-mode", "--gh-aw-ref",
            "--actions-repo",
        }:
            skip_next = True
            continue
        if arg.startswith("-"):
            continue

        path = Path(arg)
        if path.suffix == ".md":
            md_path = path if path.is_absolute() else REPO_ROOT / path
            selected.append(md_path.with_suffix(".lock.yml"))
        elif "/" not in arg and "\\" not in arg:
            selected.append(WORKFLOW_DIR / f"{arg}.lock.yml")

    if selected:
        return selected
    return sorted(WORKFLOW_DIR.glob("*.lock.yml"))


def main() -> int:
    args = sys.argv[1:]
    version_result = subprocess.run(
        ["gh", "aw", "version"], cwd=REPO_ROOT, text=True,
        capture_output=True, check=True,
    )
    version = (version_result.stdout + version_result.stderr).strip()
    if version != f"gh aw version {COMPILER_VERSION}":
        raise SystemExit(f"Expected gh-aw {COMPILER_VERSION}; found {version}")
    forbidden = {"--action-tag", "--action-mode", "--gh-aw-ref", "--actions-repo"}
    if any(arg.split("=", 1)[0] in forbidden for arg in args):
        raise SystemExit("Compiler/runtime pin overrides are not supported by this wrapper")
    subprocess.run(
        ["gh", "aw", "compile", "--action-mode", "release", "--action-tag",
         RUNTIME_COMMIT, "--no-check-update", *args],
        cwd=REPO_ROOT,
        check=True,
    )

    lockfiles = selected_lockfiles(args)
    if not lockfiles:
        return 0

    patcher = Path(__file__).with_name("patch_gh_aw_codex_endpoint.py")
    subprocess.run(
        [sys.executable, str(patcher), *(str(path) for path in lockfiles)],
        cwd=REPO_ROOT,
        check=True,
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
