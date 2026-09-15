"""GitHub-backed state with compare-and-swap updates on an isolated data branch."""

import json
from pathlib import Path
import re
import subprocess
import tempfile

from artifacts import download, validate
from state import Rejected, require

STATE_BRANCH = "automation/bugfix-state"


def run(command, cwd=None, input_text=None):
    result = subprocess.run(command, cwd=cwd, input=input_text, text=True,
                            capture_output=True, check=False)
    if result.returncode:
        raise Rejected(f"Command failed ({result.returncode}): {result.stderr.strip()}")
    return result.stdout.strip()


def github(repo, path):
    return json.loads(run(["gh", "api", f"repos/{repo}/{path}"]))


def verify_run(repo, event, allowed_workflows):
    """Verify run provenance and actual payload before recording positive evidence."""
    positive = event["outcome"] in ("behavior_correct", "pass") or (
        event["kind"] == "baseline" and event["outcome"] == "bug_present")
    workflow_run = github(repo, f"actions/runs/{event['run_id']}")
    definition = event.get("check_workflow_sha", event["workflow_sha"]) if event["kind"] != "review" else event["workflow_sha"]
    require(workflow_run.get("head_sha") == definition, "Run workflow SHA mismatch")
    require(workflow_run.get("path") in allowed_workflows, "Unexpected evidence workflow")
    require(workflow_run.get("event") == "workflow_dispatch", "Evidence requires explicit dispatch")
    require(workflow_run.get("status") == "completed", "Evidence run is incomplete")
    if positive:
        require(workflow_run.get("conclusion") == "success", "Successful evidence requires a successful run")
    artifacts = github(repo, f"actions/runs/{event['run_id']}/artifacts?per_page=100")
    require(artifacts.get("total_count", 0) <= 100, "Too many artifacts to verify without pagination")
    matching = [artifact for artifact in artifacts.get("artifacts", [])
                if artifact.get("name") == event["artifact"] and not artifact.get("expired", True)]
    require(len(matching) == 1, "Evidence artifact missing, expired, or ambiguous")
    if positive:
        hashes = validate(download(repo, matching[0]), event)
        from profiles import production_evidence, profile_complete_check
        if profile_complete_check(event, event["kind"]):
            from runs import select_artifact
            recorded = {lane["artifact"]: lane for lane in event.get("matrix", [])}
            require(set(recorded) == set(event["acceptance_profile"]["required_lanes"]), "Profile-complete evidence lacks required lanes")
            for name, lane in recorded.items():
                data = download(repo, select_artifact(artifacts["artifacts"], name))
                actual = validate(data, {**event, "artifact": name, "environment": lane["environment"]})
                require(actual["report_sha256"] == lane.get("report_sha256"), "Recorded profile lane changed")
            production, _ = production_evidence(repo, event, event["run_id"], definition)
            require(production == event.get("production"), "Profile production evidence changed")
        return hashes
    return {}


class Store:
    def __init__(self, repo):
        require(re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", repo), "Invalid GitHub repository")
        self.repo = repo

    def current_sha(self):
        result = subprocess.run(["gh", "api", f"repos/{self.repo}/git/ref/heads/{STATE_BRANCH}"],
                                text=True, capture_output=True, check=False)
        if result.returncode:
            if "(HTTP 404)" in result.stderr:
                return None
            raise Rejected(f"Cannot read state branch: {result.stderr.strip()}")
        return json.loads(result.stdout)["object"]["sha"]

    def read(self, campaign):
        sha = self.current_sha()
        if sha is None:
            return None, None
        response = github(self.repo, f"git/trees/{sha}")
        filename = f"{campaign}.json"
        entry = next((item for item in response["tree"] if item["path"] == filename), None)
        if entry is None:
            return None, sha
        import base64
        blob = github(self.repo, f"git/blobs/{entry['sha']}")
        return json.loads(base64.b64decode(blob["content"])), sha

    def write(self, state, expected_sha):
        """Commit only controller data; exact ref lease rejects competing writers."""
        with tempfile.TemporaryDirectory(prefix="litedb-bugfix-state-") as directory:
            def git(*args):
                return run(["git", "-c", "credential.helper=", "-c",
                            "credential.helper=!gh auth git-credential", *args], cwd=directory)

            git("init", "--quiet")
            git("remote", "add", "origin", f"https://github.com/{self.repo}.git")
            if expected_sha:
                git("fetch", "--quiet", "--depth=1", "origin", expected_sha)
                git("checkout", "--quiet", "--detach", expected_sha)
            path = Path(directory) / f"{state['campaign']}.json"
            path.write_text(json.dumps(state, indent=2, sort_keys=True) + "\n", encoding="utf-8")
            git("add", "--", path.name)
            if expected_sha and not git("diff", "--cached", "--name-only"):
                return expected_sha
            git("-c", "user.name=LiteDB bugfix controller", "-c", "user.email=bugfix-controller@users.noreply.github.com",
                "-c", "commit.gpgsign=false", "commit", "--quiet", "-m",
                f"Record bugfix campaign {state['campaign']}\n\nPersist verified controller state and evidence identities.")
            ref = f"refs/heads/{STATE_BRANCH}"
            git("push", "--quiet", f"--force-with-lease={ref}:{expected_sha or ''}", "origin", f"HEAD:{ref}")
            return git("rev-parse", "HEAD")
