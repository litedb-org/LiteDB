"""Shared source-provenance checks for the release compatibility scripts."""
import hashlib
import pathlib


def source_sha256(path):
    """Hash the exact LF source bytes used by the corpus producer.

    The producer records raw bytes in the manifest. A CRLF checkout would
    therefore produce a different provenance hash, so fail clearly instead of
    silently normalizing only on the verifier side.
    """
    data = pathlib.Path(path).read_bytes()
    if b"\r" in data:
        raise RuntimeError(
            f"{path} contains CRLF bytes; release corpus provenance requires an LF checkout"
        )
    return hashlib.sha256(data).hexdigest()
