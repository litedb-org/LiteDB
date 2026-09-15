"""Exact issue cases and neighboring controls for the next bounded wave."""

from dataclasses import replace
import json
from pathlib import Path
import subprocess
import unittest

from policy import load_issue, verify_focused
from trx import GateError, TestResult, TestRun


ROOT = Path(__file__).resolve().parents[2]
MANIFEST = ROOT / "scripts/bugfix/issues.json"
OBSERVED = json.loads((ROOT / "scripts/bugfix/fixtures/wave-three-baseline.json")
                      .read_text(encoding="utf-8"))
COUNTS = {1159: (1, 1), 1224: (12, 1), 2858: (4, 1), 2864: (12, 2),
          2769: (7, 5), 2225: (1, 2), 2322: (2, 1), 2873: (2, 1)}


def observed_run(number, baseline):
    tests = {}
    for row in OBSERVED["issues"][str(number)]["cases"]:
        name = row["name"]
        class_name = name.split("(", 1)[0].rsplit(".", 1)[0]
        tests[name] = TestResult(name, row["outcome"] if baseline else "Passed",
                                row["first_line"] if baseline else "", class_name)
    return TestRun(tests, "c" * 64)


class WaveThreeContracts(unittest.TestCase):
    def test_original_thirteen_contracts_are_exactly_preserved(self):
        previous = json.loads(subprocess.check_output([
            "git", "-C", str(ROOT), "show", "2275e9e8cf20b6b4c723d77ff5fb479683840a56"]))
        current = json.loads(MANIFEST.read_text(encoding="utf-8"))
        self.assertEqual(13, len(previous["issues"]))
        for number, definition in previous["issues"].items():
            if number == "2871":
                from source_context import DECLARATION
                updated = current["issues"][number]
                self.assertEqual([DECLARATION], updated.pop("source_context_observations"))
                for path, expected in DECLARATION["fixture_blobs"].items():
                    self.assertEqual(expected, updated["frozen_test_blobs"].pop(path))
                requirement = updated["review_requirements"]["lifecycle"].pop()
                self.assertIn("behavior_unverified=true and issue_credit=false", requirement)
            self.assertEqual(definition, current["issues"][number], number)

    def test_original_blobs_include_all_neighbor_control_files(self):
        for number in COUNTS:
            observation = OBSERVED["issues"][str(number)]
            issue, _ = load_issue(MANIFEST, number)
            self.assertEqual(observation["source_blobs"], issue["frozen_test_blobs"])
            self.assertEqual(observation["paths"], issue["allowed_production_paths"])
            for source, blob in observation["source_blobs"].items():
                actual = subprocess.check_output([
                    "git", "-C", str(ROOT), "rev-parse",
                    f"{OBSERVED['source_revision']}:{source}"], text=True).strip()
                self.assertEqual(blob, actual)
            for name, source in observation["neighbor_controls"].items():
                self.assertIn(source, issue["frozen_test_blobs"])
                self.assertIn({"name": name}, issue["controls"])
                self.assertIn("FullyQualifiedName=" + name, issue["filter"].split("|"))

    def test_exact_counts_baselines_and_green_candidates(self):
        for number, (failed, passed) in COUNTS.items():
            issue, _ = load_issue(MANIFEST, number)
            with self.subTest(issue=number):
                self.assertEqual((failed, passed), (len(issue["regressions"]), len(issue["controls"])))
                verify_focused(observed_run(number, True), issue, True)
                verify_focused(observed_run(number, False), issue, False)

    def test_every_regression_rejects_wrong_red_and_unexpected_pass(self):
        for number in COUNTS:
            issue, _ = load_issue(MANIFEST, number)
            for case in issue["regressions"]:
                for outcome, message in (("Failed", "System.TimeoutException"), ("Passed", "")):
                    run = observed_run(number, True)
                    run.tests[case["name"]] = replace(run.tests[case["name"]],
                                                     outcome=outcome, message=message)
                    with self.subTest(issue=number, case=case["name"]), self.assertRaises(GateError):
                        verify_focused(run, issue, True)

    def test_every_control_remains_required_before_and_after_fix(self):
        for number in COUNTS:
            issue, _ = load_issue(MANIFEST, number)
            for case in issue["controls"]:
                for baseline in (True, False):
                    run = observed_run(number, baseline)
                    run.tests[case["name"]] = replace(run.tests[case["name"]], outcome="Failed")
                    with self.subTest(issue=number, case=case["name"]), self.assertRaises(GateError):
                        verify_focused(run, issue, baseline)

    def test_missing_or_skipped_case_cannot_pass(self):
        for number in COUNTS:
            issue, _ = load_issue(MANIFEST, number)
            for name in observed_run(number, False).tests:
                for mutation in ("missing", "skip"):
                    run = observed_run(number, False)
                    if mutation == "missing":
                        del run.tests[name]
                    else:
                        run.tests[name] = replace(run.tests[name], outcome="NotExecuted")
                    with self.subTest(issue=number, case=name), self.assertRaises(GateError):
                        verify_focused(run, issue, False)

    def test_constructor_issue_includes_reported_model_and_existing_api_control(self):
        issue, _ = load_issue(MANIFEST, 2873)
        self.assertIn("LiteDB.Tests/Issues/Issue2873_ReportedModelTests.cs", issue["frozen_test_blobs"])
        self.assertTrue(any("Issue2873_ReportedModelTests." in case["name"] for case in issue["regressions"]))
        self.assertIn("Existing_ctor_mapping", issue["controls"][0]["name"])

    def test_environment_dependent_issues_keep_the_original_observation(self):
        current = json.loads(MANIFEST.read_text(encoding="utf-8"))
        for number in (2860, 2870):
            self.assertIn(str(number), current["issues"])
            evidence = OBSERVED["deferred"][str(number)]
            self.assertTrue(evidence["reference_cases"])
            self.assertTrue(any(evidence["lane_differences"].values()))
            self.assertTrue(evidence["reason"])
            self.assertEqual(current["issues"][str(number)]["regressions"],
                             evidence["resolved_focused_baseline"]["regressions"])


if __name__ == "__main__":
    unittest.main()
