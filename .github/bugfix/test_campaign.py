"""Exercise complete repair loops and restart boundaries with simulated services."""

import contextlib
import copy
import io
from pathlib import Path
from types import SimpleNamespace
import unittest
from unittest.mock import patch

from campaign import Campaign
from evidence import event_for
from state import Rejected


class MemoryStore:
    def __init__(self):
        self.state = None
        self.sha = "1" * 40  # Another issue already created the shared data branch.
        self.version = 1

    def read(self, campaign):
        return copy.deepcopy(self.state), self.sha

    def write(self, state, expected):
        if expected != self.sha:
            raise Rejected("stale state lease")
        self.version += 1
        self.sha = f"{self.version:040x}"
        self.state = copy.deepcopy(state)
        return self.sha


class SimulatedRuns:
    log = []
    fail_workers = False
    compatibility = True

    def __init__(self, repo, ref, sha, journal, save, **kwargs):
        self.journal = journal
        self.save = save

    def dispatch(self, key, workflow, inputs):
        if key not in self.journal["requests"]:
            self.journal["requests"][key] = {"run_id": 100 + len(self.journal["requests"]), "workflow": workflow,
                                                "inputs": inputs}
            self.save()
            self.log.append(("dispatch", workflow, inputs.get("role"), inputs))
        return self.journal["requests"][key]["run_id"]

    def wait(self, run_id):
        request = next(item for item in self.journal["requests"].values() if item["run_id"] == run_id)
        self.log.append(("wait", request["workflow"], request["inputs"].get("role"), {}))
        failure = self.fail_workers and request["workflow"] == "bugfix-fix.lock.yml"
        return {"id": run_id, "conclusion": "failure" if failure else "success"}

    def artifacts(self, run_id):
        return []

    def jobs(self, run_id):
        return [{"name": "compatibility", "conclusion": "success"}] if self.compatibility else []


class CampaignLoopTests(unittest.TestCase):
    def setUp(self):
        self.args = SimpleNamespace(repo="owner/repo", campaign="canary", issue=2874,
                                    integration_base="a" * 40, test_source_sha="b" * 40,
                                    workflow_sha="c" * 40, workflow_ref="automation/wholesale-bugfix",
                                    max_runs=40, timeout_minutes=180, repository=Path("repo"))
        self.store = MemoryStore()
        SimulatedRuns.log = []
        SimulatedRuns.fail_workers = False
        SimulatedRuns.compatibility = True
        self.fail_first = False

    def check(self, repo, state, workflow_run, artifacts, control):
        kind = state["phase"]
        outcome = "bug_present" if kind == "baseline" else ("behavior_correct" if kind == "focused" else "pass")
        if self.fail_first and kind == "focused" and state["repair_attempts"] == 1:
            outcome = "fail"
        return event_for(state, kind, workflow_run["id"], outcome=outcome,
                         environment="linux-x64-net8.0", artifact="check", diagnostics=["Assertion still fails"])

    def review(self, repo, state, workflow_run, artifacts, role):
        return event_for(state, "review", workflow_run["id"], role=role, findings=[], outcome="pass",
                         environment="agent", artifact="review")

    def execute(self):
        def candidate(repository, control, repo, state, source, run_id, data):
            return ("d" if state["repair_attempts"] == 0 else "e") * 40, {"patch_sha256": "hash"}

        with patch("campaign.Store", return_value=self.store), patch("campaign.Runs", SimulatedRuns), \
                patch("campaign.load_snapshot", return_value=({"schema_version": 1, "state_commit": "1" * 40,
                      "ledger_sha256": "f" * 64, "cases_sha256": "e" * 64, "case_count": 0}, [])), \
                patch("campaign.check_event", side_effect=self.check), patch("campaign.review_event", side_effect=self.review), \
                patch("campaign.select_artifact", return_value={}), patch("campaign.download", return_value=b"patch"), \
                patch("campaign.create_candidate", side_effect=candidate), patch("campaign.publish_candidate"), \
                contextlib.redirect_stdout(io.StringIO()):
            return Campaign(self.args, Path("control")).execute()

    def test_canary_reaches_ready_and_dispatches_reviews_before_waiting(self):
        state = self.execute()
        self.assertEqual("ready", state["phase"])
        self.assertEqual(1, state["repair_attempts"])
        reviews = [entry for entry in SimulatedRuns.log if entry[1] == "bugfix-validate.lock.yml"]
        self.assertEqual(["dispatch"] * 3 + ["wait"] * 3, [entry[0] for entry in reviews])
        self.assertEqual({"behavior", "compatibility", "lifecycle"}, set(state["reviews"]))
        self.assertNotIn("integration_sha", state)

    def test_failed_candidate_returns_to_worker_with_structured_feedback(self):
        self.fail_first = True
        state = self.execute()
        self.assertEqual("ready", state["phase"])
        self.assertEqual(2, state["repair_attempts"])
        fixes = [entry for entry in SimulatedRuns.log if entry[0] == "dispatch" and entry[1] == "bugfix-fix.lock.yml"]
        self.assertEqual("d" * 40, fixes[1][3]["base_sha"])
        self.assertIn("Assertion still fails", fixes[1][3]["feedback"])

    def test_failed_workers_exhaust_separate_retry_budget(self):
        SimulatedRuns.fail_workers = True
        state = self.execute()
        self.assertEqual("blocked", state["phase"])
        self.assertEqual(0, state["repair_attempts"])
        self.assertEqual(3, state["orchestration"]["worker_retries"])

    def test_ready_campaign_resumes_without_dispatch(self):
        self.execute()
        SimulatedRuns.log.clear()
        self.assertEqual("ready", self.execute()["phase"])
        self.assertEqual([], SimulatedRuns.log)

    def test_missing_compatibility_job_blocks_acceptance(self):
        SimulatedRuns.compatibility = False
        with self.assertRaisesRegex(Rejected, "compatibility"):
            self.execute()
        self.assertEqual("blocked", self.store.state["phase"])


if __name__ == "__main__":
    unittest.main()
