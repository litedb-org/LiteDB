"""Heartbeat output cannot turn missing/deferred work into an acceptance claim."""

import json
import unittest
from unittest.mock import patch

import sweep_report as report
from state import Rejected


class SweepReportTests(unittest.TestCase):
    def test_missing_manifest_reports_setup_failure_without_success_claim(self):
        text = report.render("litedb-org/LiteDB", "test", None, "a" * 40, 123, {})
        self.assertIn("has not initialized", text)
        self.assertIn("No completion is claimed", text)
        self.assertIn("/actions/runs/123", text)

    def test_deferred_and_accepted_are_distinct_and_final_matrix_not_claimed(self):
        manifest = {"kind": "hosted-bugfix-sweep", "sweep": "test", "phase": "complete",
                    "paused": False, "specification": {"workflow_sha": "b" * 40, "issues": [1002, 1506]},
                    "issues": {"1002": {"status": "deferred"}, "1506": {"status": "accepted"}}}
        text = report.render("litedb-org/LiteDB", "test", manifest, "a" * 40, 123,
                             {"1002": ("blocked-1002", {"phase": "blocked"})})
        self.assertIn("Accepted: **1**", text)
        self.assertIn("deferred: **1**", text)
        self.assertIn("final full-matrix validation is finished", text)
        self.assertIn("Deferred candidates remain unaccepted", text)
        self.assertIn("| #1002 | deferred | [blocked]", text)

    def test_updates_bot_comment_and_ignores_matching_human_comment(self):
        marker = report.marker("test") + "\nold"
        comments = [[{"id": 1, "user": {"login": "someone", "type": "User"}, "body": marker},
                     {"id": 2, "user": {"login": report.BOT, "type": "Bot"}, "body": marker}]]
        with patch("sweep_report.run", side_effect=[json.dumps(comments),
                   json.dumps({"body": "new", "html_url": "https://github.com/example"})]) as command:
            report.publish("litedb-org/LiteDB", "test", "new")
        mutation = command.call_args_list[1]
        self.assertIn("PATCH", mutation.args[0])
        self.assertIn("repos/litedb-org/LiteDB/issues/comments/2", mutation.args[0])
        self.assertEqual({"body": "new"}, json.loads(mutation.kwargs["input_text"]))

    def test_ambiguous_bot_comments_do_not_mutate_any_comment(self):
        comment = {"id": 1, "user": {"login": report.BOT, "type": "Bot"},
                   "body": report.marker("test") + "\nold"}
        with patch("sweep_report.run", return_value=json.dumps([[comment, comment]])) as command:
            with self.assertRaises(Rejected):
                report.publish("litedb-org/LiteDB", "test", "new")
        self.assertEqual(1, command.call_count)


if __name__ == "__main__":
    unittest.main()
