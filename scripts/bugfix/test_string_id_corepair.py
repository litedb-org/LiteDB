"""The expanded auto-ID contract must repair the String/ObjectId overlap."""

from collections import Counter
from dataclasses import replace
import json
from pathlib import Path
import subprocess
import unittest

from corepair_contract_assertions import EXPANDED as EVIDENCE, expected_after_auto_id_corepair
from failure_normalization import canonical_failure, load_failure_normalization
from policy import compare_ledger, load_issue, make_ledger, verify_focused
from trx import GateError, TestResult, TestRun

ROOT = Path(__file__).resolve().parents[2]
MANIFEST = ROOT / "scripts/bugfix/issues.json"


class StringIdCorepairTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.normalization, cls.policy_sha = load_failure_normalization(
            ROOT / "scripts/bugfix/failure-normalization.json")

    def issue(self):
        return load_issue(MANIFEST, 1002)[0]

    def run_for(self, baseline=True):
        tests, definitions = {}, {}
        for name, case in EVIDENCE["cases"].items():
            tests[name] = TestResult(name, case["outcome"] if baseline else "Passed",
                EVIDENCE["representative_raw_failures"][name] if baseline else "",
                case["class_name"], case["test_id"])
            definitions[case["test_id"]] = name
        return TestRun(tests, "a" * 64, definitions)

    def verify(self, run, baseline, environment="linux-x64-net8.0"):
        verify_focused(run, self.issue(), baseline, environment,
                       self.normalization, self.policy_sha)

    def test_only_the_reviewed_contract_changes(self):
        previous = json.loads(subprocess.check_output(["git", "-C", str(ROOT), "show",
                              "3e88920da5fec3636f150fa622b7ec5e75360187"]))
        current = json.loads(MANIFEST.read_text(encoding="utf-8"))
        self.assertEqual(28, len(current["issues"]))
        self.assertEqual(previous["issues"].keys(), current["issues"].keys())
        for number, contract in previous["issues"].items():
            self.assertEqual(expected_after_auto_id_corepair(number, contract),
                             current["issues"][number])
        self.assertNotIn("source_context_observations", self.issue())

    def test_original_blobs_cases_and_components_are_explicit(self):
        issue = self.issue()
        self.assertEqual((13, 3), (len(issue["regressions"]), len(issue["controls"])))
        original = EVIDENCE["superseded_contract"]
        self.assertEqual(original["allowed_production_paths"], issue["allowed_production_paths"])
        self.assertEqual(original["regressions"], issue["regressions"][:9])
        self.assertEqual(original["controls"], issue["controls"][:2])
        expected = {f"LiteDB.Tests.Issues.Issue2590_Tests."
                    f"Empty_string_ids_are_addressable_or_rejected_before_any_write(path: {path})"
                    for path in ("InsertOne", "InsertMany", "UpsertOne", "UpsertMany")}
        self.assertEqual(expected, {case["name"] for case in issue["regressions"][9:]})
        self.assertEqual("LiteDB.Tests.Database.AutoId_Tests.AutoId_BsonDocument",
                         issue["controls"][2]["name"])
        self.assertEqual(original["focused_baseline"], issue["focused_baseline"])
        self.assertEqual(original["required_environments"], issue["required_environments"])
        self.assertEqual(original["environments"], issue["environments"])
        for role, notes in original["review_requirements"].items():
            self.assertEqual(notes, issue["review_requirements"][role][:len(notes)])
        self.assertEqual([], issue["focused_baseline"]["control_gaps"])
        self.assertEqual(EVIDENCE["source_blobs"], issue["frozen_test_blobs"])
        for path, blob in issue["frozen_test_blobs"].items():
            self.assertEqual(blob, subprocess.check_output(["git", "-C", str(ROOT), "rev-parse",
                issue["frozen_test_revision"] + ":" + path], text=True).strip())

    def test_exact_baseline_and_green_candidates_for_all_environments(self):
        self.assertEqual(EVIDENCE["failure_normalization_sha256"], self.policy_sha)
        for name, case in EVIDENCE["cases"].items():
            self.assertEqual(case["canonical_failure"], canonical_failure(name,
                EVIDENCE["representative_raw_failures"][name], self.normalization,
                baseline=case["outcome"] == "Failed"))
        for environment in self.issue()["environments"]:
            self.verify(self.run_for(), True, environment)
            self.verify(self.run_for(False), False, environment)

    def test_v10_seed_remains_rejected_not_reclassified_as_fixed(self):
        run = self.run_for(False)
        for name, row in EVIDENCE["v10_diagnostic"]["cases"]["candidate"].items():
            run.tests[name] = replace(run.tests[name], outcome=row["outcome"],
                                      message=row["canonical_failure"])
        self.assertEqual(4, sum(t.outcome == "Failed" for t in run.tests.values()))
        with self.assertRaisesRegex(GateError, "Expected Passed"):
            self.verify(run, False)
        with self.assertRaises(GateError):
            self.verify(run, True)

    def test_wrong_defects_partial_fixes_and_failure_tail_changes_are_rejected(self):
        for case in self.issue()["regressions"]:
            name = case["name"]
            for baseline in (True, False):
                run = self.run_for(baseline)
                run.tests[name] = replace(run.tests[name], outcome="Failed",
                    message=run.tests[name].message + "\nWrong diagnostic or partial atomicity repair")
                with self.assertRaises(GateError):
                    self.verify(run, baseline)
            run = self.run_for()
            run.tests[name] = replace(run.tests[name], outcome="Passed", message="")
            with self.assertRaises(GateError):
                self.verify(run, True)

    def test_controls_and_inventory_cannot_be_weakened(self):
        for name in EVIDENCE["cases"]:
            for mutation in ("missing", "skip", "identity", "extra"):
                run = self.run_for(False)
                if mutation == "missing":
                    del run.tests[name]
                elif mutation == "extra":
                    run.tests[name + "extra"] = replace(run.tests[name], name=name + "extra")
                else:
                    run.tests[name] = replace(run.tests[name], **(
                        {"outcome": "NotExecuted"} if mutation == "skip" else
                        {"test_id": "00000000-0000-0000-0000-000000000000"}))
                with self.assertRaises(GateError):
                    self.verify(run, False)
        for control in self.issue()["controls"]:
            run = self.run_for(False)
            run.tests[control["name"]] = replace(run.tests[control["name"]], outcome="Failed")
            with self.assertRaises(GateError):
                self.verify(run, False)

    def test_broad_gate_credits_only_declared_co_repairs(self):
        baseline, candidate = self.run_for(), self.run_for(False)
        guard = TestResult("LiteDB.Tests.Audit2026.UnrelatedGuard", "Failed", "Original guard",
                           "LiteDB.Tests.Audit2026", "unrelated")
        for run in (baseline, candidate):
            run.tests[guard.name] = guard
            run.definitions[guard.test_id] = guard.name
        provenance = {"base_sha": "a" * 40, "test_definition_sha": "b" * 40,
                      "environment": "linux-x64-net8.0", "manifest_sha256": "c" * 64,
                      "failure_normalization_sha256": self.policy_sha}
        inventory = Counter(baseline.definitions.values())
        ledger = make_ledger(baseline, provenance, {t.class_name for t in baseline.tests.values()},
                             inventory, set(), self.normalization)
        verdict = compare_ledger(ledger, candidate, self.issue(), provenance, inventory, self.normalization)
        self.assertTrue(verdict["accepted"])
        candidate.tests[guard.name] = replace(guard, outcome="Passed", message="")
        verdict = compare_ledger(ledger, candidate, self.issue(), provenance, inventory, self.normalization)
        self.assertFalse(verdict["accepted"])
        self.assertEqual([guard.name], verdict["unexpected_passes"])


if __name__ == "__main__":
    unittest.main()
