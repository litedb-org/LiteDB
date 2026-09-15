"""Exact runtime-dependent #2367 evidence and genuine neighboring controls."""

from dataclasses import replace
import hashlib
import json
from pathlib import Path
import subprocess
import unittest

from failure_normalization import load_failure_normalization
from policy import load_issue, verify_focused, verify_focused_pair
from trx import GateError, TestResult, TestRun

ROOT = Path(__file__).resolve().parents[2]
MANIFEST = ROOT / "scripts/bugfix/issues.json"
FIXTURE = ROOT / "scripts/bugfix/fixtures/issue-2367-focused-baseline.json"


class Issue2367ContractTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.evidence = json.loads(FIXTURE.read_text(encoding="utf-8"))
        cls.normalization, cls.normalization_sha = load_failure_normalization(
            ROOT / "scripts/bugfix/failure-normalization.json")

    def issue(self):
        return load_issue(MANIFEST, 2367)[0]

    def run_for(self, environment, baseline=True):
        tests, definitions = {}, {}
        for name, case in self.evidence["cases"].items():
            row = case["observed_by_environment"][environment]
            tests[name] = TestResult(name, row["outcome"] if baseline else "Passed",
                                    row["canonical_failure"] if baseline else "",
                                    case["class_name"], case["test_id"])
            definitions[case["test_id"]] = name
        return TestRun(tests, "a" * 64, definitions)

    def verify(self, run, environment, baseline=True):
        verify_focused(run, self.issue(), baseline, environment,
                       self.normalization, self.normalization_sha)

    def test_preserves_all_previous_twenty_seven_contracts(self):
        old = json.loads(subprocess.check_output([
            "git", "-C", str(ROOT), "show", "86d80047d9fdbed6cd12140377b8bbf26a29cb9a"]))
        current = json.loads(MANIFEST.read_text(encoding="utf-8"))
        self.assertEqual(27, len(old["issues"]))
        for number, contract in old["issues"].items():
            self.assertEqual(contract, current["issues"][number], number)

    def test_original_blobs_and_exact_nonvacuous_controls(self):
        issue = self.issue()
        self.assertEqual(self.evidence["source_blobs"], issue["frozen_test_blobs"])
        self.assertEqual({"LiteDB.Tests.Mapper.LinqEval_Tests.Linq_Math_Eval",
                          "LiteDB.Tests.Mapper.LinqEval_Tests.Linq_Document_Navigation_Eval"},
                         {case["name"] for case in issue["controls"]})
        for path, blob in issue["frozen_test_blobs"].items():
            actual = subprocess.check_output(["git", "-C", str(ROOT), "rev-parse",
                issue["frozen_test_revision"] + ":" + path], text=True).strip()
            self.assertEqual(blob, actual)
        for control in issue["controls"]:
            self.assertIn("FullyQualifiedName=" + control["name"], issue["filter"].split("|"))
        self.assertNotIn("Linq_Array_Navigation_Eval", issue["filter"])
        self.assertNotIn("source_context_observations", issue)

    def test_artifacts_and_canonical_failures_bind_each_environment(self):
        issue = self.issue()
        self.assertEqual((3, 2), (len(issue["regressions"]), len(issue["controls"])))
        self.assertEqual(self.normalization_sha,
                         issue["focused_baseline"]["failure_normalization_sha256"])
        self.assertEqual([], issue["focused_baseline"]["control_gaps"])
        self.assertTrue(self.evidence["repeat_matches_baseline"])
        for artifact in self.evidence["artifacts"]:
            actual = issue["focused_baseline"]["artifacts"][artifact["environment"]]
            self.assertEqual(artifact["id"], actual["artifact_id"])
            self.assertEqual(artifact["name"], actual["artifact_name"])
            for key in ("baseline_trx_sha256", "repeat_candidate_trx_sha256"):
                self.assertEqual(artifact[key], actual[key])
        for case in issue["regressions"]:
            self.assertNotIn(case["name"], self.normalization)
            evidence = self.evidence["cases"][case["name"]]
            self.assertEqual(evidence["test_id"], case["test_id"])
            self.assertEqual({"net8.0", "net10.0"}, set(case["failure_classifications"]))
            for environment, observed in evidence["observed_by_environment"].items():
                expected = case["baseline_by_environment"][environment]
                self.assertEqual(environment.rsplit("-", 1)[1], expected["classification"])
                classified = case["failure_classifications"][expected["classification"]]
                failure = observed["canonical_failure"]
                self.assertEqual(failure.splitlines()[0], classified["failure_first_line"])
                self.assertEqual(hashlib.sha256(failure.encode()).hexdigest(), classified["sha256"])

    def test_all_observed_baselines_and_green_candidate_identities(self):
        for environment in self.issue()["environments"]:
            baseline = self.run_for(environment)
            candidate = self.run_for(environment, False)
            self.verify(baseline, environment)
            self.verify(candidate, environment, False)
            verify_focused_pair(baseline, candidate, self.issue())

    def test_wrong_runtime_and_changed_full_failure_rejected(self):
        for environment in self.issue()["environments"]:
            other = "linux-x64-net10.0" if environment.endswith("net8.0") else "linux-x64-net8.0"
            for case in self.issue()["regressions"]:
                for mutation in ("runtime", "details", "pass"):
                    run = self.run_for(environment)
                    original = run.tests[case["name"]]
                    replacement = self.run_for(other).tests[case["name"]].message
                    if mutation == "details":
                        replacement = original.message + "\nUnexpected extra diagnostic"
                    run.tests[case["name"]] = replace(original, message=replacement,
                        outcome="Passed" if mutation == "pass" else "Failed")
                    with self.subTest(environment=environment, mutation=mutation), self.assertRaises(GateError):
                        self.verify(run, environment)

    def test_controls_missing_skipped_failed_or_renamed_identity_rejected(self):
        environment = "linux-x64-net8.0"
        for name in self.evidence["cases"]:
            for baseline in (True, False):
                for mutation in ("missing", "skip", "id"):
                    run = self.run_for(environment, baseline)
                    if mutation == "missing":
                        del run.tests[name]
                    else:
                        run.tests[name] = replace(run.tests[name], **(
                            {"outcome": "NotExecuted"} if mutation == "skip" else
                            {"test_id": "00000000-0000-0000-0000-000000000000"}))
                    with self.assertRaises(GateError):
                        self.verify(run, environment, baseline)
        for case in self.issue()["controls"]:
            run = self.run_for(environment)
            run.tests[case["name"]] = replace(run.tests[case["name"]], outcome="Failed")
            with self.assertRaises(GateError):
                self.verify(run, environment)

    def test_extra_vacuous_control_or_wrong_policy_cannot_pass(self):
        environment = "linux-x64-net8.0"
        run = self.run_for(environment)
        run.tests["vacuous"] = TestResult("LiteDB.Tests.Mapper.LinqEval_Tests.Linq_Array_Navigation_Eval",
                                         "Passed", "", "LiteDB.Tests.Mapper.LinqEval_Tests")
        with self.assertRaises(GateError):
            self.verify(run, environment)
        with self.assertRaisesRegex(GateError, "policy or environment"):
            verify_focused(self.run_for(environment), self.issue(), True, environment,
                           self.normalization, "0" * 64)


if __name__ == "__main__":
    unittest.main()
