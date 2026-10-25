import json
import os
import shutil
import tempfile
import unittest
from pathlib import Path

import pr_evidence as evidence
from safety_fixtures import GitRepo, csharp_class, run_quietly, script_closure
from test_regression_proof import BASE, LEDGER, REPRO, entry, ledger, run_report

SHA = "b" * 40
TESTS = "LiteDB.Tests/Engine/Added_Tests.cs"


class BadgeTests(unittest.TestCase):
    def test_badge_names_tests_and_proofs(self):
        self.assertEqual(evidence.badge(12, 0, 2, 2, False, 1), "Evidence: +12 tests · 2/2 proven to fail before")
        self.assertEqual(evidence.badge(3, 1, 1, 2, True, 2), "Evidence: +3 tests (−1) · 1/2 proven to fail before")
        self.assertEqual(evidence.badge(0, 0, 0, 0, True, 0), "Evidence: +0 tests · bug fix without regression proof")
        self.assertEqual(evidence.badge(5, 0, 0, 0, False, 0), "Evidence: +5 tests · no regression proof")

    def test_labels_follow_the_evidence_only_as_far_as_the_trusted_side_confirms(self):
        base = {"pr": 1, "headSha": SHA, "testsAdded": 1, "testsRemoved": 0, "newProofs": 1, "bug": False}
        passing = {**base, "proofsTotal": 1, "proofsProven": 1}
        cases = [
            ("bug fix proven", passing, True, True, False, {evidence.PROVEN: True, evidence.NEEDS_PROOF: False}),
            ("proof failed", {**base, "proofsTotal": 1, "proofsProven": 0}, True, True, False,
             {evidence.PROVEN: False, evidence.NEEDS_PROOF: True}),
            ("no new proof", {**base, "newProofs": 0, "proofsTotal": 0, "proofsProven": 0}, True, True, False,
             {evidence.PROVEN: False, evidence.NEEDS_PROOF: True}),
            ("not a bug", {**base, "proofsTotal": 0, "proofsProven": 0}, False, True, False,
             {evidence.PROVEN: False, evidence.NEEDS_PROOF: False}),
            ("run failed although evidence claims success", passing, True, False, False,
             {evidence.PROVEN: False, evidence.NEEDS_PROOF: True}),
            ("harness changed by the PR", passing, True, True, True,
             {evidence.PROVEN: False, evidence.NEEDS_PROOF: True}),
            ("forged bug flag in evidence is ignored", {**passing, "bug": True}, False, True, True,
             {evidence.PROVEN: False, evidence.NEEDS_PROOF: False}),
        ]
        for label, data, bug, succeeded, harness, expected in cases:
            with self.subTest(label):
                self.assertEqual(evidence.wanted_labels(data, bug, succeeded, harness), expected)

    def test_harness_changes_are_recognized(self):
        self.assertTrue(evidence.harness_changed([".github/scripts/pr_evidence.py"]))
        self.assertTrue(evidence.harness_changed([".github/scripts/repro_scaffold.py"]))
        self.assertTrue(evidence.harness_changed(["LiteDB.ReproRunner/LiteDB.ReproRunner.Cli/Program.cs"]))
        self.assertFalse(evidence.harness_changed(["LiteDB.ReproRunner/Repros/Issue_1/Program.cs",
                                                   ".github/safety/regression-proofs.json", "LiteDB/Engine/X.cs"]))

    def test_the_harness_list_covers_the_whole_import_closure(self):
        self.assertEqual(script_closure("pr_evidence"),
                         {path for path in evidence.HARNESS_FILES if path.startswith(".github/scripts/")})

    def test_labels_command_reads_trusted_inputs(self):
        directory = Path(tempfile.mkdtemp())
        try:
            data = {"pr": 3, "headSha": SHA, "testsAdded": 1, "testsRemoved": 0, "proofsTotal": 1,
                    "proofsProven": 1, "newProofs": 1, "bug": False}
            (directory / "files.txt").write_text("LiteDB/Engine/X.cs\n", encoding="utf-8")
            for labels, proof_count, expected in (
                    ("bug\narea: storage\n", 1, {evidence.PROVEN: True, evidence.NEEDS_PROOF: False}),
                    ("bugfix-fix\n", 0, {evidence.PROVEN: True, evidence.NEEDS_PROOF: True}),
                    ("enhancement\n", 0, {evidence.PROVEN: True, evidence.NEEDS_PROOF: False})):
                with self.subTest(labels):
                    (directory / "e.json").write_text(json.dumps({**data, "newProofs": proof_count}), encoding="utf-8")
                    (directory / "labels.txt").write_text(labels, encoding="utf-8")
                    code, text = run_quietly(evidence.main, [
                        "labels", "--evidence", str(directory / "e.json"), "--pr-labels", str(directory / "labels.txt"),
                        "--changed-files", str(directory / "files.txt"), "--run-conclusion", "success"])
                    self.assertEqual(code, 0, text)
                    self.assertEqual(json.loads(text)["labels"], expected)
        finally:
            shutil.rmtree(directory)

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
                    "--reports", str(reports), "--labels", '["bugfix-fix"]', "--output", str(output)])
                self.assertEqual(code, 0, text)
                self.assertIn("Evidence: +2 tests · 1/1 proven to fail before", text)
                data = json.loads(output.read_text())
                self.assertEqual((data["testsAdded"], data["proofsProven"], data["newProofs"], data["pr"], data["bug"]),
                                 (2, 1, 1, 7, True))
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
