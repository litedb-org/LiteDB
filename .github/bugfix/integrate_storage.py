"""Atomic controller-data commits and a persistent global integration lock."""

import base64
import json
from pathlib import Path, PurePosixPath
import tempfile
import uuid

from state import require
from storage import STATE_BRANCH, Store, github, run

LOCK_NAME = "integration-lock"
LEDGER_NAME = "accepted-tests"


def encoded(value):
    return (json.dumps(value, indent=2, sort_keys=True) + "\n").encode("utf-8")


class IntegrationStore:
    def __init__(self, repo):
        self.repo = repo
        self.store = Store(repo)

    def read(self, name):
        return self.store.read(name)

    def read_at(self, name, sha):
        tree = github(self.repo, f"git/trees/{sha}")
        item = next((entry for entry in tree["tree"] if entry["path"] == name + ".json"), None)
        if item is None:
            return None
        blob = github(self.repo, f"git/blobs/{item['sha']}")
        return json.loads(base64.b64decode(blob["content"]))

    def commit(self, expected_sha, files, message):
        """One exact-lease data commit preserves evidence before branch mutation."""
        require(expected_sha is not None, "Integration requires an existing controller state branch")
        require(sum(len(value) for value in files.values()) <= 512 * 1024 * 1024, "Durable evidence exceeds 512 MiB")
        with tempfile.TemporaryDirectory(prefix="litedb-integration-data-") as directory:
            root = Path(directory).resolve()

            def git(*arguments):
                return run(["git", "-c", "credential.helper=", "-c",
                            "credential.helper=!gh auth git-credential", *arguments], cwd=root)

            git("init", "--quiet")
            git("remote", "add", "origin", f"https://github.com/{self.repo}.git")
            git("fetch", "--quiet", "--depth=1", "origin", expected_sha)
            git("checkout", "--quiet", "--detach", expected_sha)
            for name, content in files.items():
                relative = PurePosixPath(name)
                require(not relative.is_absolute() and ".." not in relative.parts
                        and ".git" not in relative.parts and "\\" not in name and ":" not in name, "Unsafe evidence path")
                require(len(content) <= 95 * 1024 * 1024, "An evidence file exceeds GitHub's blob limit")
                path = root.joinpath(*relative.parts)
                require(path.resolve().is_relative_to(root) and not path.is_symlink(), "Evidence destination escapes data checkout")
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(content)
                git("add", "--", name)
            git("-c", "user.name=LiteDB bugfix controller", "-c", "user.email=bugfix-controller@users.noreply.github.com",
                "-c", "commit.gpgsign=false", "commit", "--quiet", "-m", message)
            ref = f"refs/heads/{STATE_BRANCH}"
            git("push", "--quiet", f"--force-with-lease={ref}:{expected_sha}", "origin", f"HEAD:{ref}")
            return git("rev-parse", "HEAD")

    def acquire(self, identity, resume=False):
        previous, sha = self.read(LOCK_NAME)
        if previous and previous.get("active"):
            require(resume, f"Integration lock is active for {previous.get('campaign')}; explicit --resume required")
            require(previous.get("identity") == identity, "Another integration owns the global lock")
            return previous
        require(not resume, "No active integration exists to resume")
        lock = {"schema_version": 1, "active": True, "campaign": identity["campaign"],
                "token": uuid.uuid4().hex, "identity": identity, "phase": "acquired"}
        self.commit(sha, {LOCK_NAME + ".json": encoded(lock)},
                    f"Lock integration for {identity['campaign']}\n\nSerialize the exact tested candidate branch update.")
        return lock

    def require_lock(self, token):
        lock, sha = self.read(LOCK_NAME)
        require(lock and lock.get("active") and lock.get("token") == token, "Integration lock changed")
        return lock, sha
