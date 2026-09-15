#!/usr/bin/env python3
"""Reject generated bugfix workers with GitHub write permissions or write tools."""

from __future__ import annotations

import re
import sys
from pathlib import Path


WORKERS = {"bugfix-agent-canary.lock.yml", "bugfix-fix.lock.yml", "bugfix-validate.lock.yml"}
ALLOWED_TOOLS = {"noop", "record_completion"}


def validate_readonly(text: str) -> None:
    """Check the compiler's generated block-style YAML without a YAML dependency."""
    permission_indent = None
    permission_blocks = 0
    tool_lists = 0
    for line in text.splitlines():
        stripped = line.strip()
        if not stripped or stripped.startswith("#"):
            continue
        indent = len(line) - len(line.lstrip())
        if permission_indent is not None and indent <= permission_indent:
            permission_indent = None
        header = re.fullmatch(r"permissions:\s*(.*)", stripped)
        if header:
            permission_blocks += 1
            value = header.group(1)
            if value not in {"", "{}", "read-all"}:
                raise ValueError("Worker has writable or unrecognized inline permissions")
            permission_indent = indent if not value else None
            continue
        if permission_indent is not None:
            if not re.fullmatch(r"[a-z][a-z-]*:\s*(read|none)(?:\s+#.*)?", stripped):
                raise ValueError("Worker has writable or unrecognized job permissions")
        if stripped.startswith("Tools: "):
            tool_lists += 1
            tools = set(stripped.removeprefix("Tools: ").split(", "))
            if not tools or not tools <= ALLOWED_TOOLS:
                raise ValueError("Worker exposes a publishing safe-output tool")
    if permission_blocks < 2 or tool_lists != 1:
        raise ValueError("Generated worker structure changed; review permissions and tools")
    forbidden = ("create_issue", "create_report_incomplete_issue", "create_pull_request",
                 "add_comment", "push_to_pull_request_branch", "merge_pull_request")
    if any(name in text for name in forbidden):
        raise ValueError("Worker includes a GitHub publication handler")


def check_lockfile(path: Path) -> None:
    if path.name in WORKERS:
        validate_readonly(path.read_text(encoding="utf-8"))


if __name__ == "__main__":
    for argument in sys.argv[1:]:
        check_lockfile(Path(argument))
