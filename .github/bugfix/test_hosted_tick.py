"""Short ticks keep uncertain dispatches and publications on their original request."""

import copy
from pathlib import Path
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, patch

from campaign import Campaign
from errors import InfrastructureError
from patching import CandidateConflict
from runs import Pending, Runs
from state import Rejected, new_state
from test_runs import DispatchTests


class NonblockingRunTests(DispatchTests):
    def setUp(self):
        super().setUp()
        self.runs.nonblocking = True

    def test_new_dispatch_returns_pending_without_sleep_and_later_finds_same_run(self):
        with patch("runs.github", return_value={"object": {"sha": self.workflow_sha}}), \
                patch("runs.run") as send, patch.object(self.runs, "_find", return_value=None), patch("runs.time.sleep") as sleep:
            self.assertIsNone(self.runs.dispatch("new", "check.yml", {}))
            self.assertIsNone(self.runs.dispatch("new", "check.yml", {}))
            self.assertEqual(1, send.call_count)
            sleep.assert_not_called()
        with patch.object(self.runs, "_find", return_value=self.run), patch("runs.run") as send:
            self.assertEqual(123, self.runs.dispatch("new", "check.yml", {}))
            send.assert_not_called()

    def test_pending_run_never_waits_and_completed_run_is_consumed(self):
        self.journal["requests"]["key"] = {"workflow": "check.yml", "request_id": self.request_id, "run_id": 123, "started_at": 100}
        with patch("runs.time.time", return_value=101), patch("runs.github", return_value={**self.run, "status": "in_progress"}), patch("runs.time.sleep") as sleep:
            with self.assertRaises(Pending):
                self.runs.wait(123)
            sleep.assert_not_called()
        with patch("runs.github", return_value={**self.run, "status": "completed", "conclusion": "success"}):
            self.assertEqual("success", self.runs.wait(123)["conclusion"])

    def test_budget_cooldown_keeps_old_request_and_dispatches_once_after_reset(self):
        self.journal["requests"]["fix"] = {"workflow": "check.yml", "inputs": {}, "run_id": 99,
                                             "budget": {"resume_after": 200}}
        with patch("runs.time.time", return_value=100), patch("runs.run") as send:
            with self.assertRaises(Pending):
                self.runs.dispatch("fix", "check.yml", {})
            send.assert_not_called()
        with patch("runs.time.time", return_value=201), patch("runs.github", return_value={"object": {"sha": self.workflow_sha}}), \
                patch("runs.run") as send, patch.object(self.runs, "_find", return_value=self.run):
            self.assertEqual(123, self.runs.dispatch("fix", "check.yml", {}))
            self.assertEqual(123, self.runs.dispatch("fix", "check.yml", {}))
            self.assertEqual(1, send.call_count)
        self.assertEqual(99, self.journal["requests"]["fix"]["run_id"])

    def test_uncertain_dispatch_never_blindly_retries(self):
        self.journal["requests"]["key"] = {"workflow": "check.yml", "inputs": {}, "request_id": self.request_id, "started_at": 0}
        with patch("runs.time.time", return_value=601), patch.object(self.runs, "_find", return_value=None), patch("runs.run") as send:
            with self.assertRaisesRegex(Rejected, "uncertain"):
                self.runs.dispatch("key", "check.yml", {})
            send.assert_not_called()


class TickCampaignTests(unittest.TestCase):
    def campaign(self):
        campaign = Campaign.__new__(Campaign)
        campaign.args = SimpleNamespace(repo="owner/repo", repository=Path("repo"), campaign="canary")
        campaign.control = Path("control")
        campaign.state = new_state("canary", 2874, "a" * 40, "b" * 40, "c" * 40)
        campaign.state["phase"] = "repairing"
        campaign.state["orchestration"] = {"requests": {"fix-1-0": {"run_id": 123}}, "worker_retries": 0}
        campaign.runs = Mock(journal=campaign.state["orchestration"])
        campaign.runs.dispatch.return_value = 123
        campaign.runs.wait.return_value = {"id": 123, "conclusion": "success"}
        campaign.refresh = Mock()
        campaign.save = Mock()
        campaign.record = Mock()
        return campaign

    def test_publication_transport_retries_same_worker_without_local_sha_journal(self):
        campaign = self.campaign()
        with patch("campaign.repair_feedback", return_value=""), patch("campaign.select_artifact", return_value={}), \
                patch("campaign.download", return_value=b"patch"), patch("campaign.create_candidate", return_value=("d" * 40, {})), \
                patch("campaign.publish_candidate", side_effect=[InfrastructureError("Readback unavailable after push"), "d" * 40]) as publish:
            with self.assertRaises(Pending):
                campaign.repair()
            request = campaign.state["orchestration"]["requests"]["fix-1-0"]
            self.assertNotIn("candidate_sha", request)
            self.assertEqual(0, campaign.state["orchestration"]["worker_retries"])
            campaign.repair()
            self.assertEqual("d" * 40, request["candidate_sha"])
            self.assertEqual(publish.call_args_list[0], publish.call_args_list[1])
            self.assertEqual(1, campaign.record.call_count)

    def test_remote_ref_conflict_never_requests_another_worker(self):
        campaign = self.campaign()
        with patch("campaign.repair_feedback", return_value=""), patch("campaign.select_artifact", return_value={}), \
                patch("campaign.download", return_value=b"patch"), patch("campaign.create_candidate", return_value=("d" * 40, {})), \
                patch("campaign.publish_candidate", side_effect=CandidateConflict("Wrong remote identity")):
            with self.assertRaises(CandidateConflict):
                campaign.repair()
        self.assertEqual(0, campaign.state["orchestration"]["worker_retries"])
        self.assertNotIn("candidate_sha", campaign.state["orchestration"]["requests"]["fix-1-0"])

    def test_one_tick_never_advances_a_second_phase_or_blocks_a_pending_run(self):
        campaign = self.campaign()
        campaign.advance_phase = Mock(side_effect=Pending("running"))
        campaign.record_failure = Mock()
        self.assertIs(campaign.state, campaign.advance_once())
        self.assertEqual(1, campaign.advance_phase.call_count)
        campaign.record_failure.assert_not_called()


if __name__ == "__main__":
    unittest.main()
