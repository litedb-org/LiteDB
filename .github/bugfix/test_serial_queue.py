"""Serial execution delegates validation and integration to the pinned existing CLIs."""

from contextlib import nullcontext
from pathlib import Path
import unittest
from unittest.mock import Mock, patch

from orchestrate import TEST_SOURCE
import serial_queue
from state import Rejected


class SerialQueueTests(unittest.TestCase):
    def setUp(self):
        self.args = serial_queue.arguments(["--repo", "owner/repo", "--workflow-sha", "c" * 40,
                                           "--workflow-ref", "automation/runtime", "--campaign-prefix", "batch",
                                           "--issues", "2874", "2839"])
        self.contracts = {str(issue): {"inventory_issue": issue, "frozen_test_revision": TEST_SOURCE,
                          "regressions": [{"name": "LiteDB.Tests.Regression"}], "controls": [{"name": "LiteDB.Tests.Control"}],
                          "allowed_production_paths": ["LiteDB/Production.cs"]} for issue in self.args.issues}
        self.manifest = {"issues": self.contracts}
        self.control = Path("trusted-runtime")

    def decision(self, issue, phase="baseline", base="a" * 40, action="start"):
        return {"issue": issue, "campaign": f"batch-{issue}", "action": action, "phase": phase, "base_sha": base,
                "candidate_sha": "d" * 40 if issue == 2874 else "e" * 40, "data_sha": "f" * 40,
                "resume_integration": False}

    def test_each_issue_uses_new_base_and_only_existing_perfix_clis(self):
        decisions = []
        for issue, base in ((2874, "a" * 40), (2839, "d" * 40)):
            decisions += [self.decision(issue, base=base), self.decision(issue, "ready", base, "resume"),
                          self.decision(issue, base=base, action="skip_accepted")]
        with patch("serial_queue.pinned_manifest", return_value=self.manifest), \
                patch("serial_queue.git"), patch("serial_queue.worktree", return_value=nullcontext(self.control)), \
                patch("serial_queue.inspect_queue_issue", side_effect=decisions), patch("serial_queue.invoke") as invoke:
            result = serial_queue.execute(self.args)
        self.assertEqual("complete", result["phase"])
        calls = invoke.call_args_list
        self.assertEqual(["orchestrate.py", "integrate.py", "integrate.py"] * 2, [call.args[0].name for call in calls])
        for index, base in ((0, "a" * 40), (3, "d" * 40)):
            command = calls[index].args[1]
            self.assertEqual(base, command[command.index("--integration-base") + 1])
            self.assertEqual(str(self.control / ".github/bugfix/orchestrate.py"), str(calls[index].args[0]))
        self.assertNotIn("--apply", calls[1].args[1])
        self.assertIn("--apply", calls[2].args[1])

    def test_resume_ready_uses_same_candidate_and_existing_integration_lock(self):
        ready = self.decision(2874, "ready", action="resume")
        ready["resume_integration"] = True
        accepted = {**ready, "action": "skip_accepted"}
        with patch("serial_queue.inspect_queue_issue", side_effect=[ready, ready, accepted]), \
                patch("serial_queue.invoke") as invoke:
            serial_queue.run_issue(self.args, 2874, self.contracts, self.control)
        self.assertEqual(["integrate.py", "integrate.py"], [call.args[0].name for call in invoke.call_args_list])
        self.assertIn("--resume", invoke.call_args_list[1].args[1])

    def test_failed_campaign_stops_before_next_issue_or_integration(self):
        with patch("serial_queue.pinned_manifest", return_value=self.manifest), \
                patch("serial_queue.git"), patch("serial_queue.worktree", return_value=nullcontext(self.control)), \
                patch("serial_queue.inspect_queue_issue", return_value=self.decision(2874)) as inspect, \
                patch("serial_queue.invoke", side_effect=Rejected("blocked")) as invoke:
            with self.assertRaisesRegex(Rejected, "blocked"):
                serial_queue.execute(self.args)
        self.assertEqual(1, inspect.call_count)
        self.assertEqual(1, invoke.call_count)

    def test_dry_run_performs_no_mutating_operations(self):
        self.args.dry_run = True
        decisions = [self.decision(issue) for issue in self.args.issues]
        with patch("serial_queue.pinned_manifest", return_value=self.manifest), \
                patch("serial_queue.inspect_queue_issue", side_effect=decisions), patch("serial_queue.git") as git, \
                patch("serial_queue.worktree") as worktree, patch("serial_queue.invoke") as invoke:
            result = serial_queue.execute(self.args)
        git.assert_not_called()
        worktree.assert_not_called()
        invoke.assert_not_called()
        self.assertFalse(result["mutations"])
        self.assertFalse(result["full_ci"])
        self.assertEqual("resolved after preceding issue integration", result["issues"][1]["base_sha"])

    def test_unapproved_or_duplicate_issue_list_never_starts(self):
        self.args.issues.append(9999)
        with patch("serial_queue.pinned_manifest", return_value=self.manifest), patch("serial_queue.git") as git:
            with self.assertRaisesRegex(Rejected, "no approved contract"):
                serial_queue.execute(self.args)
        git.assert_not_called()
        with self.assertRaisesRegex(Rejected, "distinct"):
            serial_queue.arguments(["--repo", "owner/repo", "--workflow-sha", "c" * 40, "--workflow-ref", "runtime",
                                    "--campaign-prefix", "batch", "--issues", "2874", "2874"])


if __name__ == "__main__":
    unittest.main()
