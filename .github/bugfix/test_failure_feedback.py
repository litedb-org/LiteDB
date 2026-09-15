"""Completed assertion failures must return to repair after TRX identity mapping."""

from pathlib import Path
import sys
import unittest
import xml.etree.ElementTree as ET

from evidence import _failure_outcome
from test_artifacts import archive

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts/bugfix"))
from test_support import target_run, xml_report


class FailureFeedbackTests(unittest.TestCase):
    def classify(self, run):
        data = archive({
            "baseline-verdict.json": {"accepted": True, "outcome": "bug_present"},
            "candidate/execution.json": {"runs": {"focused": 1}},
            "candidate/focused.trx": ET.tostring(xml_report(run)),
        })
        return _failure_outcome(data, ROOT, {"issue": 2874, "phase": "focused"})

    def test_complete_expected_cases_return_assertion_feedback(self):
        outcome, diagnostics = self.classify(target_run())
        self.assertEqual("fail", outcome)
        self.assertEqual(6, len(diagnostics))
        self.assertTrue(all(item["test"].startswith("LiteDB.Tests.Issues.Issue2874_Tests.")
                            for item in diagnostics))

    def test_missing_case_remains_a_harness_error(self):
        run = target_run()
        run.tests.pop(next(iter(run.tests)))
        outcome, _ = self.classify(run)
        self.assertEqual("harness_error", outcome)


if __name__ == "__main__":
    unittest.main()
