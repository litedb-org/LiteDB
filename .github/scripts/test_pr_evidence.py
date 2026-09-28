import json
import os
import shutil
import tempfile
import unittest
from pathlib import Path

import pr_evidence as evidence
from safety_fixtures import GitRepo, csharp_class, run_quietly
from test_regression_proof import BASE, LEDGER, REPRO, entry, ledger, run_report

SHA = "b" * 40
TESTS = "LiteDB.Tests/Engine/Added_Tests.cs"


class BadgeTests(unittest.TestCase):
    def test_badge_names_tests_and_proofs(self):
        self.assertEqual(evidence.badge(12, 0, 2, 2, False, 1), "Evidence: +12 tests · 2/2 proven to fail before")
        self.assertEqual(evidence.badge(3, 1, 1, 2, True, 2), "Evidence: +3 tests (−1) · 1/2 proven to fail before")
        self.assertEqual(evidence.badge(0, 0, 0, 0, True, 0), "Evidence: +0 tests · bug fix without regression proof")
        self.assertEqual(evidence.badge(5, 0, 0, 0, False, 0), "Evidence: +5 tests · no regression proof")

    def test_labels_follow_the_evidence(self):
        base = {"pr": 1, "headSha": SHA, "testsAdded": 1, "testsRemoved": 0, "newProofs": 1, "bug": True}
        self.assertEqual(evidence.wanted_labels({**base, "proofsTotal": 1, "proofsProven": 1}),
                         {evidence.PROVEN: True, evidence.NEEDS_PROOF: False})
        self.assertEqual(evidence.wanted_labels({**base, "proofsTotal": 1, "proofsProven": 0}),
                         {evidence.PROVEN: False, evidence.NEEDS_PROOF: True})
        self.assertEqual(evidence.wanted_labels({**base, "newProofs": 0, "proofsTotal": 0, "proofsProven": 0}),
                         {evidence.PROVEN: False, evidence.NEEDS_PROOF: True})
        self.assertEqual(evidence.wanted_labels({**base, "bug": False, "proofsTotal": 0, "proofsProven": 0}),
                         {evidence.PROVEN: False, evidence.NEEDS_PROOF: False})

    def test_untrusted_evidence_is_type_checked(self):
        valid = {"pr": 1, "headSha": SHA, "testsAdded": 1, "testsRemoved": 0, "proofsTotal": 1,
                 "proofsProven": 1, "newProofs": 1, "bug": False}
        self.assertEqual(evidence.parse_evidence(json.dumps(valid))["pr"], 1)
        for broken in ({**valid, "pr": "1; rm -rf /"}, {**valid, "headSha": "main"}, {**valid, "bug": "yes"},
                       {**valid, "testsAdded": -1}, {**valid, "proofsProven": 2}, {**valid, "pr": True}, [valid]):
            with self.subTest(broken=broken), self.assertRaises(ValueError):
                evidence.parse_evidence(json.dumps(broken))


class SummarizeTests(unittest.TestCase):
    def test_summarize_counts_new_tests_and_proven_proofs(self):
        reports = Path(tempfile.mkdtemp())
        try:
            folder = reports / f"regression-proof-{REPRO}"
            folder.mkdir()
            (folder / "proof.json").write_text(json.dumps(run_report()), encoding="utf-8")
            matrix = json.dumps({"include": [{"repro": REPRO, "version": "5.0.20"}]})
            with GitRepo() as repo:
                base = repo.commit({**BASE, LEDGER: ledger()})
                repo.commit({LEDGER: ledger(entry()), TESTS: csharp_class("Added_Tests", {
                    "One": ("Fact", ""), "Two": ("Theory", "")})})
                output = repo.path / "pr-evidence.json"
                code, text = run_quietly(evidence.main, [
                    "summarize", "--base", base, "--head-sha", SHA, "--pr", "7", "--matrix", matrix,
                    "--reports", str(reports), "--bug", "--output", str(output)])
                self.assertEqual(code, 0, text)
                self.assertIn("Evidence: +2 tests · 1/1 proven to fail before", text)
                data = json.loads(output.read_text())
                self.assertEqual((data["testsAdded"], data["proofsProven"], data["newProofs"], data["pr"]), (2, 1, 1, 7))
        finally:
            shutil.rmtree(reports)

    def test_a_missing_or_failing_report_is_not_proven(self):
        reports = Path(tempfile.mkdtemp())
        try:
            folder = reports / f"regression-proof-{REPRO}"
            folder.mkdir()
            (folder / "proof.json").write_text(json.dumps(run_report(package_actual=1)), encoding="utf-8")
            matrix = [{"repro": REPRO, "version": "5.0.20"}, {"repro": "Issue_2_Missing", "version": "5.0.20"}]
            self.assertEqual(evidence.tally(reports, matrix), 0)
        finally:
            shutil.rmtree(reports)


if __name__ == "__main__":
    unittest.main()
