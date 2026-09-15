"""Acceptance requires all six actual matrix reports, not one green lane."""

import unittest
from unittest.mock import patch

from evidence import MATRIX, broad_failure_outcome, check_event, event_for
from state import Rejected, apply_event, new_state
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

    def test_failure_on_nonrepresentative_lane_blocks_instead_of_repairing(self):
        name = MATRIX[-1]
        self.reports[name] = {"broad-verdict.json": {"accepted": False, "provenance": {"environment": "macos-arm64-net10.0"}}}
        with patch("evidence.download", side_effect=lambda repo, item: archive(self.reports[item["name"]])), \
                patch("evidence._failure_outcome", return_value=("inconclusive", [{"name": "ExistingFailure"}])):
            event = check_event("owner/repo", self.state, {"id": 123, "conclusion": "failure"}, self.artifacts, None)
        self.assertEqual("inconclusive", event["outcome"])
        self.assertEqual(6, len(event["failed_matrix"]))
        self.assertEqual(name, event["diagnostics"][0]["artifact"])
        self.assertTrue(all(item["report_sha256"] for item in event["failed_matrix"]))

    def test_wrong_platform_and_stale_lane_rejected(self):
        self.reports[MATRIX[-1]]["verdict.json"]["environment"] = "linux-x64-net10.0"
        with self.assertRaisesRegex(Rejected, "matrix lane"):
            self.check()
        self.reports[MATRIX[-1]]["verdict.json"]["environment"] = "macos-x64-net10.0"
        self.reports[MATRIX[-1]]["verdict.json"]["candidate_sha"] = "e" * 40
        with self.assertRaisesRegex(Rejected, "candidate_sha"):
            self.check()


class FailureRoutingTests(unittest.TestCase):
    def setUp(self):
        self.change = {"name": "Issues.ExistingStackTest", "class_name": "Issues",
                       "baseline_failure_sha256": "a" * 64,
                       "candidate_failure_sha256": "b" * 64}
        self.report = {"accepted": False, "outcome": "inconclusive",
                       "errors": ["Known failure classification changed: Issues.ExistingStackTest"],
                       "classification_changes": [self.change]}

    def test_existing_failure_drift_blocks_instead_of_dispatching_repair(self):
        outcome, diagnostics = broad_failure_outcome(self.report)
        state = new_state("canary", 2874, "a" * 40, "b" * 40, "c" * 40)
        state.update(phase="broad", candidate_sha="d" * 40, repair_attempts=1)
        event = event_for(state, "broad", 123, outcome=outcome, diagnostics=diagnostics,
                          artifact="broad", environment="linux-x64-net8.0")
        result = apply_event(state, event)
        self.assertEqual("blocked", result["phase"])
        self.assertEqual(1, result["repair_attempts"])
        self.assertEqual([self.change], result["evidence"]["broad"]["diagnostics"])

    def test_malformed_drift_cannot_be_treated_as_code_failure(self):
        for field, value in (("name", ""), ("class_name", None),
                             ("baseline_failure_sha256", "A" * 64),
                             ("candidate_failure_sha256", "a" * 64)):
            with self.subTest(field=field):
                changed = {**self.change, field: value}
                self.assertEqual("harness_error", broad_failure_outcome(
                    {**self.report, "classification_changes": [changed]})[0])

    def test_actual_regression_remains_a_repair_failure(self):
        report = {"errors": ["Previously passing test failed: Issues.Control"],
                  "classification_changes": [], "outcome": "inconclusive"}
        self.assertEqual("fail", broad_failure_outcome(report)[0])


if __name__ == "__main__":
    unittest.main()
