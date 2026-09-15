"""Resumable dispatch correlation and bounded polling for trusted workflows."""

import re
import time
from urllib.parse import quote
import uuid

from state import require
from storage import github, run


def match_run(runs, request_id, workflow_sha, workflow_ref):
    require(re.fullmatch(r"bf-[0-9a-f]{32}", request_id), "Invalid controller request ID")
    matches = [item for item in runs if request_id in item.get("display_title", "").split()
               and item.get("head_sha") == workflow_sha and item.get("head_branch") == workflow_ref
               and item.get("event") == "workflow_dispatch"]
    require(len(matches) <= 1, "Multiple runs claim the same controller request ID")
    return matches[0] if matches else None


class Runs:
    def __init__(self, repo, workflow_ref, workflow_sha, journal, save, max_runs=40, timeout_minutes=180):
        self.repo = repo
        self.workflow_ref = workflow_ref
        self.workflow_sha = workflow_sha
        self.journal = journal
        self.save = save
        self.max_runs = max_runs
        self.timeout_seconds = timeout_minutes * 60

    def _find(self, workflow, request_id):
        path = f"actions/workflows/{quote(workflow, safe='')}/runs?event=workflow_dispatch&per_page=100"
        response = github(self.repo, path)
        return match_run(response["workflow_runs"], request_id, self.workflow_sha, self.workflow_ref)

    def dispatch(self, key, workflow, inputs):
        requests = self.journal.setdefault("requests", {})
        if key in requests:
            request = requests[key]
            require(request["workflow"] == workflow and request["inputs"] == inputs, "Pending dispatch inputs changed")
            if "run_id" in request:
                return request["run_id"]
            found = self._find(workflow, request["request_id"])
            require(found is not None, "Previous dispatch is uncertain; inspect GitHub before resuming")
        else:
            require(len(requests) < self.max_runs, "Campaign workflow-run budget exhausted")
            ref = github(self.repo, f"git/ref/heads/{quote(self.workflow_ref, safe='/')}")
            require(ref["object"]["sha"] == self.workflow_sha, "Workflow branch moved; refusing stale dispatch")
            request = {"request_id": "bf-" + uuid.uuid4().hex, "workflow": workflow,
                       "inputs": inputs, "started_at": time.time()}
            requests[key] = request
            self.save()
            arguments = ["gh", "workflow", "run", workflow, "--repo", self.repo, "--ref", self.workflow_ref]
            for name, value in {**inputs, "request_id": request["request_id"]}.items():
                arguments.extend(["--raw-field", f"{name}={value}"])
            print(f"Dispatch {workflow}: {request['request_id']}", flush=True)
            run(arguments)
            found = None
            for _ in range(24):
                found = self._find(workflow, request["request_id"])
                if found:
                    break
                time.sleep(5)
            require(found is not None, "Dispatch did not produce a discoverable run; do not redispatch blindly")
        request["run_id"] = found["id"]
        self.save()
        print(f"Run https://github.com/{self.repo}/actions/runs/{found['id']}", flush=True)
        return found["id"]

    def wait(self, run_id):
        request = next(item for item in self.journal["requests"].values() if item.get("run_id") == run_id)
        previous = None
        while True:
            evidence = github(self.repo, f"actions/runs/{run_id}")
            require(evidence.get("head_sha") == self.workflow_sha, "Run workflow SHA changed")
            require(evidence.get("event") == "workflow_dispatch", "Unexpected run trigger")
            require(evidence.get("path") == f".github/workflows/{request['workflow']}", "Unexpected run workflow")
            require(request["request_id"] in evidence.get("display_title", "").split(), "Run request ID mismatch")
            status = evidence.get("status")
            if status != previous:
                print(f"Run {run_id}: {status} {evidence.get('conclusion') or ''}".rstrip(), flush=True)
                previous = status
            if status == "completed":
                return evidence
            require(time.time() - request["started_at"] < self.timeout_seconds, "Workflow exceeded campaign timeout")
            time.sleep(20)

    def artifacts(self, run_id):
        response = github(self.repo, f"actions/runs/{run_id}/artifacts?per_page=100")
        require(response["total_count"] <= 100, "Too many workflow artifacts")
        return response["artifacts"]

    def jobs(self, run_id):
        response = github(self.repo, f"actions/runs/{run_id}/jobs?per_page=100")
        require(response["total_count"] <= 100, "Too many workflow jobs")
        return response["jobs"]


def select_artifact(artifacts, name):
    matching = [item for item in artifacts if item.get("name") == name and not item.get("expired", True)]
    require(len(matching) == 1, f"Required artifact missing, expired, or ambiguous: {name}")
    return matching[0]
