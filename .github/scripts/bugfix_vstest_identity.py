"""Read the actual VSTest testhost architecture from retained diagnostics."""

import hashlib
from pathlib import Path
import re


MAX_DIAGNOSTIC_BYTES = 64 * 1024 * 1024


def normalized_architecture(value):
    aliases = {"amd64": "x64", "x86_64": "x64", "x64": "x64",
               "x86": "x86", "i386": "x86", "i686": "x86",
               "arm64": "arm64", "aarch64": "arm64"}
    return aliases.get(value.lower()) if isinstance(value, str) else None


def diagnostic_identity(raw):
    if not raw or len(raw) > MAX_DIAGNOSTIC_BYTES:
        raise ValueError("VSTest diagnostic is missing or exceeds its size bound")
    architectures = {
        normalized_architecture(match.decode("ascii"))
        for match in re.findall(
            rb"DotnetTestHostmanager\.GetTestHostProcessStartInfo: .*?"
            rb"target architecture '([A-Za-z0-9_]+)'", raw)
    }
    if None in architectures or len(architectures) != 1:
        raise ValueError("VSTest did not report one exact testhost architecture")
    return {"testhost_architecture": architectures.pop(),
            "sha256": hashlib.sha256(raw).hexdigest()}


def read_diagnostic(path):
    return diagnostic_identity(Path(path).read_bytes())
