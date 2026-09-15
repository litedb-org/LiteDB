"""Dispatch correlation must survive restart without creating duplicate workers."""

import contextlib
import io
import unittest
from unittest.mock import Mock, patch

import orchestrate
from runs import Runs, match_run
from state import Rejected


class DispatchTests(unittest.TestCase):
    def setUp(self):
        self.request_id = "bf-" + "a" * 32
        self.workflow_sha = "c" * 40
        self.ref = "automation/wholesale-bugfix"
        self.run = {"id": 123, "display_title": f"Bugfix {self.request_id}", "head_sha": self.workflow_sha,
                    "head_branch": self.ref, "event": "workflow_dispatch", "path": ".github/workflows/check.yml"}
        self.journal = {"requests": {}}
        self.save = Mock()
        self.runs = Runs("owner/repo", self.ref, self.workflow_sha, self.journal, self.save)

    def test_exact_token_revision_and_branch_required(self):
        self.assertEqual(self.run, match_run([self.run], self.request_id, self.workflow_sha, self.ref))
        for field, value in (("display_title", "prefix" + self.request_id), ("head_sha", "d" * 40),
                             ("head_branch", "other"), ("event", "push")):
            with self.subTest(field=field):
                modified = {**self.run, field: value}
                self.assertIsNone(match_run([modified], self.request_id, self.workflow_sha, self.ref))
        with self.assertRaises(Rejected):
            match_run([self.run, self.run], self.request_id, self.workflow_sha, self.ref)

    def test_dispatch_journal_is_written_before_remote_action(self):
        order = []
        self.runs.save = lambda: order.append("save")
        with patch("runs.github", return_value={"object": {"sha": self.workflow_sha}}), \
                patch("runs.run", side_effect=lambda command: order.append("dispatch")), \
                patch.object(self.runs, "_find", return_value=self.run), contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(123, self.runs.dispatch("baseline-0", "check.yml", {"level": "baseline"}))
        self.assertEqual(["save", "dispatch", "save"], order)

    def test_resuming_known_run_does_not_dispatch(self):
        self.journal["requests"]["key"] = {"workflow": "check.yml", "inputs": {}, "run_id": 123}
        with patch("runs.github") as github, patch("runs.run") as command:
            self.assertEqual(123, self.runs.dispatch("key", "check.yml", {}))
        github.assert_not_called()
        command.assert_not_called()

    def test_uncertain_dispatch_never_blindly_retries(self):
        self.journal["requests"]["key"] = {"workflow": "check.yml", "inputs": {}, "request_id": self.request_id}
        with patch.object(self.runs, "_find", return_value=None), patch("runs.run") as command:
            with self.assertRaisesRegex(Rejected, "uncertain"):
                self.runs.dispatch("key", "check.yml", {})
        command.assert_not_called()

    def test_moved_workflow_branch_and_exhausted_budget_reject_before_dispatch(self):
        with patch("runs.github", return_value={"object": {"sha": "d" * 40}}), patch("runs.run") as command:
            with self.assertRaisesRegex(Rejected, "moved"):
                self.runs.dispatch("key", "check.yml", {})
        command.assert_not_called()
        self.runs.max_runs = 0
        with self.assertRaisesRegex(Rejected, "budget"):
            self.runs.dispatch("key", "check.yml", {})

    def test_run_timeout_is_bounded_without_more_sleep(self):
        self.journal["requests"]["key"] = {"workflow": "check.yml", "request_id": self.request_id,
                                             "run_id": 123, "started_at": 0}
        with patch("runs.github", return_value={**self.run, "status": "in_progress"}), \
                patch("runs.time.sleep") as sleep, contextlib.redirect_stdout(io.StringIO()):
            with self.assertRaisesRegex(Rejected, "timeout"):
                self.runs.wait(123)
        sleep.assert_not_called()

    def test_dry_run_has_no_remote_or_repository_side_effects(self):
        args = ["--repo", "owner/repo", "--campaign", "canary", "--issue", "2874",
                "--integration-base", "a" * 40, "--workflow-sha", "b" * 40,
                "--workflow-ref", self.ref, "--dry-run"]
        with patch("orchestrate.git") as git, patch("orchestrate.Campaign") as campaign, \
                contextlib.redirect_stdout(io.StringIO()) as output:
            self.assertEqual(0, orchestrate.main(args))
        self.assertIn('"integrates": false', output.getvalue())
        git.assert_not_called()
        campaign.assert_not_called()


if __name__ == "__main__":
    unittest.main()
