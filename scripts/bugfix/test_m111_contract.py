"""The UInt64 co-repair is one exact behavioral target, never a pass exception."""

from collections import Counter
from dataclasses import replace
import json
from pathlib import Path
import subprocess
import unittest

from policy import compare_ledger, load_issue, make_ledger
from trx import GateError, TestResult, TestRun

ROOT = Path(__file__).resolve().parents[2]
MANIFEST = ROOT / "scripts/bugfix/issues.json"
EVIDENCE = json.loads((ROOT / "scripts/bugfix/fixtures/m111-co-repair-baseline.json").read_text(encoding="utf-8"))
NAME = "LiteDB.Tests.Audit2026.ExpressionAuditRegression_Tests.M111_uint64_round_trips_through_bson_value"
FAILURE = "System.InvalidCastException : Unable to cast object of type 'System.Double' to type 'System.UInt64'."


class M111ContractTests(unittest.TestCase):
    def test_exact_addition_preserves_all_other_contracts_and_existing_cases(self):
        before = json.loads(subprocess.check_output([
            "git", "-C", str(ROOT), "show", EVIDENCE["original_contract_revision"] + ":scripts/bugfix/issues.json"]))
        current = json.loads(MANIFEST.read_text(encoding="utf-8"))
        self.assertEqual(21, len(before["issues"]))
        contract = current["issues"]["1224"]
        approved = json.loads(subprocess.check_output([
            "git", "-C", str(ROOT), "show",
            "dc7c03ac7c44cbe8708d9961f5d53fab7780a10e:scripts/bugfix/issues.json"]))["issues"]["1224"]
        self.assertEqual(approved, contract)
        self.assertEqual({"name": NAME, "failure_first_line": FAILURE}, contract["regressions"].pop())
        self.assertEqual(EVIDENCE["frozen_test_blob"], contract["frozen_test_blobs"].pop(EVIDENCE["frozen_test_path"]))
        suffix = "|FullyQualifiedName=" + NAME
        self.assertTrue(contract["filter"].endswith(suffix))
        contract["filter"] = contract["filter"][:-len(suffix)]
        self.assertIn("all six permanently accepted #2869", contract["review_requirements"]["behavior"].pop())
        self.assertIn("precision already lost", contract["review_requirements"]["compatibility"].pop())
        for number, definition in before["issues"].items():
            self.assertEqual(definition, current["issues"][number], number)
        self.assertEqual({key: value for key, value in before.items() if key != "issues"},
                         {key: value for key, value in current.items() if key != "issues"})

    def test_all_six_observations_bind_original_frozen_case_and_artifact_hash(self):
        wave = json.loads((ROOT / "scripts/bugfix/fixtures/wave-three-baseline.json").read_text(encoding="utf-8"))
        self.assertEqual(34988724926, EVIDENCE["run_id"])
        self.assertEqual(wave["source_revision"], EVIDENCE["source_revision"])
        self.assertEqual(wave["artifacts"], [{k: lane[k] for k in ("id", "name", "baseline_trx_sha256")} for lane in EVIDENCE["lanes"]])
        self.assertEqual({os + "-" + framework for os in ("linux-x64", "windows-x64", "macos-arm64")
                          for framework in ("net8.0", "net10.0")}, {lane["environment"] for lane in EVIDENCE["lanes"]})
        for lane in EVIDENCE["lanes"]:
            self.assertEqual((NAME, "Failed", FAILURE), (lane["test_name"], lane["outcome"], lane["failure_first_line"]))
            self.assertRegex(lane["baseline_trx_sha256"], r"^[0-9a-f]{64}$")
        blob = subprocess.check_output(["git", "-C", str(ROOT), "rev-parse",
                                       EVIDENCE["source_revision"] + ":" + EVIDENCE["frozen_test_path"]], text=True).strip()
        self.assertEqual(EVIDENCE["frozen_test_blob"], blob)

    def comparison(self, extra_name=None, extra_pass=False):
        issue, _ = load_issue(MANIFEST, 1224)
        before, after = {}, {}
        for case in issue["regressions"] + issue["controls"]:
            name = case["name"]
            before[name] = TestResult(name, "Failed" if "failure_first_line" in case else "Passed",
                                      case.get("failure_first_line", ""), name.split("(", 1)[0].rsplit(".", 1)[0], name)
            after[name] = replace(before[name], outcome="Passed", message="")
        if extra_name:
            failure = EVIDENCE["excluded_source_guard"]["failure_first_line"] if extra_name == EVIDENCE["excluded_source_guard"]["name"] else "Frozen unrelated defect"
            before[extra_name] = TestResult(extra_name, "Failed", failure,
                                           extra_name.split("(", 1)[0].rsplit(".", 1)[0], extra_name)
            after[extra_name] = replace(before[extra_name], outcome="Passed" if extra_pass else "Failed")
        definitions = {name: name for name in before}
        provenance = {"issue": 1224, "base_sha": "a" * 40, "candidate_sha": "b" * 40,
                      "test_definition_sha": EVIDENCE["source_revision"], "environment": "linux-x64-net8.0",
                      "manifest_sha256": "c" * 64, "failure_normalization_sha256": "d" * 64}
        ledger = make_ledger(TestRun(before, "e" * 64, definitions), provenance,
                             {t.class_name for t in before.values()}, Counter(definitions.values()), set(), {})
        return issue, ledger, TestRun(after, "f" * 64, definitions.copy()), provenance

    def compare(self, issue, ledger, candidate, provenance):
        return compare_ledger(ledger, candidate, issue, provenance, Counter(candidate.definitions.values()), {})

    def test_explicit_behavioral_transition_passes_without_source_observation(self):
        report = self.compare(*self.comparison())
        self.assertTrue(report["accepted"])
        self.assertEqual([], report["source_context_changes"])
        self.assertEqual([], report["unexpected_passes"])

    def test_m111_was_unexpected_under_the_original_contract(self):
        issue, ledger, candidate, provenance = self.comparison()
        issue["regressions"] = [case for case in issue["regressions"] if case["name"] != NAME]
        self.assertEqual([NAME], self.compare(issue, ledger, candidate, provenance)["unexpected_passes"])

    def test_source_guard111_and_other_behavioral_passes_remain_blocked(self):
        for name in (EVIDENCE["excluded_source_guard"]["name"],
                     "LiteDB.Tests.Audit2026.ExpressionAuditRegression_Tests.H29_sum_and_average_do_not_overflow_int32"):
            with self.subTest(name=name):
                report = self.compare(*self.comparison(name, True))
                self.assertFalse(report["accepted"])
                self.assertEqual([name], report["unexpected_passes"])

    def test_m111_missing_skipped_failed_or_duplicate_cannot_be_accepted(self):
        for outcome in ("missing", "NotExecuted", "Failed", "duplicate"):
            issue, ledger, candidate, provenance = self.comparison()
            if outcome == "missing":
                del candidate.tests[NAME]
            elif outcome == "duplicate":
                candidate.tests["duplicate"] = replace(candidate.tests[NAME], test_id="duplicate")
                candidate.definitions["duplicate"] = NAME
            else:
                candidate.tests[NAME] = replace(candidate.tests[NAME], outcome=outcome)
            with self.subTest(outcome=outcome), self.assertRaises(GateError):
                self.compare(issue, ledger, candidate, provenance)


if __name__ == "__main__":
    unittest.main()
