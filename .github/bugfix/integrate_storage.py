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

    def read_prefix_at(self, prefix, sha):
        """Read one controller-written evidence directory from an immutable data commit."""
        relative = PurePosixPath(prefix)
        require(not relative.is_absolute() and relative.parts and ".." not in relative.parts
                and ".git" not in relative.parts and "\\" not in prefix and ":" not in prefix,
                "Unsafe evidence prefix")
        tree_sha = sha
        for part in relative.parts:
            tree = github(self.repo, f"git/trees/{tree_sha}")
            matches = [entry for entry in tree.get("tree", []) if entry.get("path") == part]
            require(len(matches) == 1 and matches[0].get("type") == "tree", "Prepared evidence prefix is missing")
            tree_sha = matches[0]["sha"]
        pending = [(PurePosixPath(), tree_sha)]
        files = {}
        total = 0
        object_count = 0
        while pending:
            directory, current_sha = pending.pop()
            tree = github(self.repo, f"git/trees/{current_sha}")
            require(tree.get("truncated") is not True, "Prepared evidence tree is truncated")
            for entry in tree.get("tree", []):
                object_count += 1
                require(object_count <= 1000, "Prepared evidence tree contains too many objects")
                name = entry.get("path")
                require(isinstance(name, str) and name and "/" not in name and "\\" not in name
                        and name not in (".", ".."), "Unsafe prepared evidence path")
                path = directory / name
                require(len(path.parts) <= 32, "Prepared evidence path is too deep")
                if entry.get("type") == "tree":
                    pending.append((path, entry["sha"]))
                    continue
                require(entry.get("type") == "blob" and entry.get("mode") == "100644",
                        "Prepared evidence contains an unsupported Git object")
                size = entry.get("size")
                require(type(size) is int and 0 <= size <= 95 * 1024 * 1024,
                        "Prepared evidence file exceeds the supported bound")
                require(len(files) < 500, "Prepared evidence contains too many files")
                blob = github(self.repo, f"git/blobs/{entry['sha']}")
                require(blob.get("encoding") == "base64", "Prepared evidence blob encoding changed")
                encoded_content = blob.get("content")
                require(isinstance(encoded_content, str), "Prepared evidence blob content is missing")
                content = base64.b64decode("".join(encoded_content.split()), validate=True)
                require(len(content) == size, "Prepared evidence blob size changed")
                total += size
                require(total <= 512 * 1024 * 1024, "Prepared evidence exceeds 512 MiB")
                key = path.as_posix()
                require(key not in files, "Duplicate prepared evidence path")
                files[key] = content
        require(files, "Prepared evidence archive is empty")
        return files

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
