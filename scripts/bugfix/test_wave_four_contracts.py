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
OBSERVED = json.loads((ROOT / "scripts/bugfix/fixtures/wave-four-baseline.json")
                      .read_text(encoding="utf-8"))
COUNTS = {2746: (1, 3), 2807: (11, 2), 1829: (3, 3), 1444: (4, 5)}


def observed_run(number, baseline):
    tests = {}
    for row in OBSERVED["issues"][str(number)]["cases"]:
        name = row["name"]
        class_name = name.split("(", 1)[0].rsplit(".", 1)[0]
        tests[name] = TestResult(name, row["outcome"] if baseline else "Passed",
                                row["first_line"] if baseline else "", class_name)
    return TestRun(tests, "c" * 64)


class WaveFourContracts(unittest.TestCase):
    def test_original_twenty_one_contracts_are_exactly_preserved(self):
        previous = json.loads(subprocess.check_output([
            "git", "-C", str(ROOT), "show", "58440a15d433caacbd44af2599127f9f3e13b6f4"]))
        current = json.loads(MANIFEST.read_text(encoding="utf-8"))
        self.assertEqual(21, len(previous["issues"]))
        self.assertEqual(set(previous["issues"]) | {str(number) for number in COUNTS} | {"2860", "2870", "2367"},
                         set(current["issues"]))
        approved_m111 = json.loads(subprocess.check_output([
            "git", "-C", str(ROOT), "show",
            "dc7c03ac7c44cbe8708d9961f5d53fab7780a10e:scripts/bugfix/issues.json"]))["issues"]["1224"]
        for number, definition in previous["issues"].items():
            expected = approved_m111 if number == "1224" else definition
            self.assertEqual(expected, current["issues"][number], number)
        approved_wave = json.loads(subprocess.check_output([
            "git", "-C", str(ROOT), "show",
            "1aeb49ad5e1447c6268d2321a660e007be689656:scripts/bugfix/issues.json"]))
        for number in COUNTS:
            self.assertEqual(approved_wave["issues"][str(number)], current["issues"][str(number)])
        approved_environments = json.loads(subprocess.check_output([
            "git", "-C", str(ROOT), "show",
            "877f406a695bf2c686ccf368c2a3e195f7e28810:scripts/bugfix/issues.json"]))
        for number in ("2860", "2870"):
            self.assertEqual(approved_environments["issues"][number], current["issues"][number])

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

    def test_extra_selected_case_is_not_silently_accepted(self):
        for number in COUNTS:
            issue, _ = load_issue(MANIFEST, number)
            for baseline in (True, False):
                run = observed_run(number, baseline)
                run.tests["extra"] = TestResult("extra", "Passed", "", "Other")
                with self.subTest(issue=number), self.assertRaises(GateError):
                    verify_focused(run, issue, baseline)

    def test_filter_selects_every_case_and_no_unrelated_neighbor_method(self):
        for number in COUNTS:
            issue, _ = load_issue(MANIFEST, number)
            def selected(name):
                method = name.split("(", 1)[0]
                return any(method == term.split("=", 1)[1] if term.startswith("FullyQualifiedName=")
                           else term.split("~", 1)[1] in method for term in issue["filter"].split("|"))
            self.assertTrue(all(selected(name) for name in observed_run(number, True).tests))
            self.assertFalse(selected("LiteDB.Tests.QueryTest.QueryApi_Tests.Query_And_Same_Field"))
            self.assertFalse(selected("LiteDB.Tests.Issues.Issue2367_Tests.Other"))

    def test_held_guards_and_environment_difference_are_not_authorized(self):
        current = json.loads(MANIFEST.read_text(encoding="utf-8"))
        for number in (2801, 2767, 2819):
            self.assertNotIn(str(number), current["issues"])
            self.assertTrue(OBSERVED["held"][str(number)]["source_guard_ids"])
        self.assertTrue(OBSERVED["held"]["2367"]["lane_differences"])
        self.assertIn("focused_baseline", current["issues"]["2367"])
        self.assertNotIn("source_context_observations", current["issues"]["2367"])
        for number in COUNTS:
            self.assertNotIn("source_context_observations", current["issues"][str(number)])


if __name__ == "__main__":
    unittest.main()
