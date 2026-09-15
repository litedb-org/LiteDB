"""Acceptance requires all six actual matrix reports, not one green lane."""

import unittest
from unittest.mock import patch

from evidence import MATRIX, check_event
from state import Rejected, new_state
from test_artifacts import archive


class AcceptanceTests(unittest.TestCase):
    def setUp(self):
        self.state = new_state("canary", 2874, "a" * 40, "b" * 40, "c" * 40)
        self.state.update(phase="acceptance", candidate_sha="d" * 40)
        self.artifacts = [{"name": name, "expired": False} for name in MATRIX]
        self.reports = {}
        for name in MATRIX:
            framework = name.rsplit("-", 1)[1]
            os_name = "linux" if "ubuntu" in name else ("windows" if "windows" in name else "macos")
            verdict = {key: self.state[key] for key in ("issue", "base_sha", "candidate_sha", "test_source_sha", "workflow_sha")}
            verdict.update(schema_version=1, accepted=True, environment=f"{os_name}-x64-{framework}",
                           level="acceptance", outcome="behavior_correct")
            scope = {"accepted": True, "outcome": "scope_verified", "provenance":
                     {key: self.state[key] for key in ("issue", "base_sha", "candidate_sha")}}
            self.reports[name] = {"verdict.json": verdict, "scope.json": scope}

    def check(self):
        with patch("evidence.download", side_effect=lambda repo, item: archive(self.reports[item["name"]])):
            return check_event("owner/repo", self.state, {"id": 123, "conclusion": "success"}, self.artifacts, None)

    def test_all_matrix_payloads_are_recorded(self):
        event = self.check()
        self.assertEqual(6, len(event["matrix"]))
        self.assertTrue(all(item["report_sha256"] for item in event["matrix"]))

    def test_missing_lane_cannot_accept(self):
        self.artifacts.pop()
        with self.assertRaisesRegex(Rejected, "artifact missing"):
            self.check()

    def test_wrong_platform_and_stale_lane_rejected(self):
        self.reports[MATRIX[-1]]["verdict.json"]["environment"] = "linux-x64-net10.0"
        with self.assertRaisesRegex(Rejected, "matrix lane"):
            self.check()
        self.reports[MATRIX[-1]]["verdict.json"]["environment"] = "macos-x64-net10.0"
        self.reports[MATRIX[-1]]["verdict.json"]["candidate_sha"] = "e" * 40
        with self.assertRaisesRegex(Rejected, "candidate_sha"):
            self.check()


if __name__ == "__main__":
    unittest.main()
