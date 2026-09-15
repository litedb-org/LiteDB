"""One hosted owner at a time; every sweep journal update uses the data ref lease."""

import copy
import time
import uuid

from integrate_storage import IntegrationStore, encoded
from state import NAME, SHA, require
from storage import github

LOCK = "sweep-lock"
HOST_WORKFLOW = ".github/workflows/bugfix-sweep.yml"


class SweepStore:
    def __init__(self, repo, sweep, run_id=None, run_attempt=1, scheduler_sha=None):
        require(NAME.fullmatch(sweep), "Invalid sweep name")
        self.repo, self.sweep = repo, sweep
        self.name = "sweep-" + sweep
        self.owner = {"run_id": run_id, "run_attempt": run_attempt}
        self.scheduler_sha = scheduler_sha
        self.store = IntegrationStore(repo)
        self.token = None

    def read(self):
        return self.store.read(self.name)

    def acquire(self):
        require(type(self.owner["run_id"]) is int and self.owner["run_id"] > 0
                and type(self.owner["run_attempt"]) is int and self.owner["run_attempt"] > 0, "Hosted run identity required")
        current = github(self.repo, f"actions/runs/{self.owner['run_id']}")
        require(current.get("path") == HOST_WORKFLOW and current.get("status") == "in_progress"
                and current.get("run_attempt") == self.owner["run_attempt"]
                and current.get("event") in ("schedule", "workflow_run", "workflow_dispatch"), "Untrusted or stale scheduler owner")
        repository = github(self.repo, "")
        require(current.get("head_branch") == repository.get("default_branch")
                and current.get("head_sha"), "Scheduler owner must execute the default-branch host workflow")
        definition = github(self.repo, f"contents/{HOST_WORKFLOW}?ref={current['head_sha']}")
        require(definition.get("type") == "file" and SHA.fullmatch(definition.get("sha", "")), "Missing authenticated bootstrap workflow blob")
        self.bootstrap_blob_sha = definition["sha"]
        previous, sha = self.store.read(LOCK)
        require(sha is not None, "Controller state branch must already exist")
        if previous and previous.get("active"):
            old = previous["owner"]
            run = github(self.repo, f"actions/runs/{old['run_id']}")
            require(run.get("status") == "completed" or run.get("run_attempt", 0) > old["run_attempt"],
                    "Another live Actions run owns the sweep lock")
        self.token = uuid.uuid4().hex
        lock = {"schema_version": 1, "active": True, "sweep": self.sweep, "owner": self.owner,
                "token": self.token, "bootstrap_blob_sha": self.bootstrap_blob_sha, "scheduler_sha": self.scheduler_sha, "host_sha": current["head_sha"], "acquired_at": time.time(), "previous_token": previous.get("token") if previous else None}
        self.store.commit(sha, {LOCK + ".json": encoded(lock)}, "Acquire hosted sweep tick lease")

    def locked_snapshot(self):
        lock, sha = self.store.read(LOCK)
        require(lock and lock.get("active") and lock.get("token") == self.token
                and lock.get("owner") == self.owner, "Hosted sweep lease changed")
        return lock, self.store.read_at(self.name, sha), sha

    def save(self, manifest, expected):
        _, current, sha = self.locked_snapshot()
        require(current == expected, "Sweep manifest changed concurrently")
        self.store.commit(sha, {self.name + ".json": encoded(manifest)}, f"Advance hosted sweep {self.sweep}")
        return copy.deepcopy(manifest)

    def release(self):
        lock, _, sha = self.locked_snapshot()
        lock.update(active=False, released_at=time.time())
        self.store.commit(sha, {LOCK + ".json": encoded(lock)}, "Release hosted sweep tick lease")
