#!/usr/bin/env python3
"""Redact CODEX_LB_BASE_URL values from gh-aw artifacts before upload."""

from __future__ import annotations

import os
import sys
from pathlib import Path
from urllib.parse import urlparse


REPLACEMENT = b"[REDACTED_CODEX_LB]"


def redaction_needles(endpoint: str) -> list[bytes]:
    parsed = urlparse(endpoint)
    candidates = {value for value in (endpoint, parsed.netloc, parsed.hostname) if value}
    if parsed.hostname and parsed.port:
        candidates.add(f"{parsed.hostname}:{parsed.port}")
    return [candidate.encode() for candidate in sorted(candidates, key=len, reverse=True)]


def redact_file(path: Path, needles: list[bytes]) -> bool:
    try:
        data = path.read_bytes()
    except OSError:
        return False
    if b"\0" in data[:4096]:
        return False

    redacted = data
    for needle in needles:
        redacted = redacted.replace(needle, REPLACEMENT)
    if redacted == data:
        return False

    path.write_bytes(redacted)
    return True


def redact_tree(root: Path, needles: list[bytes]) -> int:
    if not root.exists():
        return 0

    count = 0
    for path in root.rglob("*"):
        if path.is_file() and redact_file(path, needles):
            count += 1
    return count


def main() -> int:
    endpoint = os.environ.get("CODEX_LB_BASE_URL", "").strip().rstrip("/")
    if not endpoint:
        return 0

    needles = redaction_needles(endpoint)
    if not needles:
        return 0

    roots = [Path(arg) for arg in sys.argv[1:]] or [Path("/tmp/gh-aw")]
    redacted_count = sum(redact_tree(root, needles) for root in roots)
    print(f"Redacted Codex endpoint artifacts: {redacted_count} file(s)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
