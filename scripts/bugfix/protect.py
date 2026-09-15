"""Limit candidate edits using trusted policy and immutable Git objects."""

import subprocess

from policy import require_sha
from trx import GateError


def git(repository, *arguments):
    result = subprocess.run(["git", "-C", str(repository), *arguments],
                            capture_output=True, check=True)
    return result.stdout.decode("utf-8")


def verify_changes(repository, base_sha, candidate_sha, issue):
    require_sha(base_sha)
    require_sha(candidate_sha)
    for sha in (base_sha, candidate_sha):
        if git(repository, "rev-parse", "--verify", sha + "^{commit}").strip() != sha:
            raise GateError(f"Not an immutable commit: {sha}")
    if git(repository, "merge-base", base_sha, candidate_sha).strip() != base_sha:
        raise GateError("Candidate does not descend from the pinned integration base")
    verify_frozen_tests(repository, base_sha, issue)
    verify_frozen_tests(repository, candidate_sha, issue)
    changes = git(repository, "diff", "--name-status", "--no-renames", "-z",
                  base_sha, candidate_sha).rstrip("\0").split("\0")
    if changes == [""]:
        raise GateError("Candidate contains no changes")
    if len(changes) % 2:
        raise GateError("Malformed Git change listing")
    allowed = set(issue["allowed_production_paths"])
    checked = []
    for status, path in zip(changes[::2], changes[1::2]):
        production = path in allowed and status == "M"
        added_test = status == "A" and path.startswith("LiteDB.Tests/") and path.endswith(".cs")
        if not production and not added_test:
            raise GateError(f"Candidate changes a protected or unapproved path: {status} {path}")
        tree_entry = git(repository, "ls-tree", candidate_sha, "--", path).split(" ", 1)[0]
        if tree_entry != "100644":
            raise GateError(f"Candidate file is not an ordinary non-executable file: {path}")
        checked.append(path)
    return checked


def verify_frozen_tests(repository, source_sha, issue):
    """The original regression blobs stay identical to the reviewed source revision."""
    require_sha(source_sha)
    require_sha(issue["frozen_test_revision"])
    for path, expected_blob in issue["frozen_test_blobs"].items():
        require_sha(expected_blob)
        if git(repository, "rev-parse", f"{source_sha}:{path}").strip() != expected_blob:
            raise GateError(f"Original regression source changed: {path}")
    return sorted(issue["frozen_test_blobs"])
