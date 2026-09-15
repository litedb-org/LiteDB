"""Keep the remaining wave bound to preserved original baseline observations."""

from dataclasses import replace
import json
from pathlib import Path
import subprocess
import unittest

from policy import load_issue, verify_focused
from trx import GateError, TestResult, TestRun


ROOT = Path(__file__).resolve().parents[2]
MANIFEST = ROOT / "scripts/bugfix/issues.json"
OBSERVED = json.loads((ROOT / "scripts/bugfix/fixtures/next-seven-baseline.json")
                      .read_text(encoding="utf-8"))
COUNTS = {2770: (1, 1), 2867: (3, 1), 2845: (7, 1), 2847: (2, 2),
          2779: (5, 2), 2205: (1, 1), 2871: (1, 1)}


def observed_run(number, baseline):
    tests = {}
    class_name = f"LiteDB.Tests.Issues.Issue{number}_Tests"
    for row in OBSERVED["issues"][str(number)]["cases"]:
        name = row["name"]
        tests[name] = TestResult(name, row["outcome"] if baseline else "Passed",
                                row["first_line"] if baseline else "", class_name)
    return TestRun(tests, "c" * 64)


class NextSevenContracts(unittest.TestCase):
    def test_existing_six_contracts_are_exactly_preserved(self):
        previous = json.loads(subprocess.check_output([
            "git", "-C", str(ROOT), "show", "ca58ec8bcaa0dcc91f9f222f587d66f77112c7cf"]))
        current = json.loads(MANIFEST.read_text(encoding="utf-8"))
        self.assertEqual(6, len(previous["issues"]))
        for number, contract in previous["issues"].items():
            with self.subTest(issue=number):
                self.assertEqual(contract, current["issues"][number])

    def test_frozen_sources_scopes_and_required_environments(self):
        frozen = "dd937719f7eee53c512f50ac604cab639bf42a4c"
        self.assertEqual(frozen, OBSERVED["source_revision"])
        self.assertEqual(34988724926, OBSERVED["run_id"])
        self.assertEqual(6, len({item["id"] for item in OBSERVED["artifacts"]}))
        for number in COUNTS:
            case = OBSERVED["issues"][str(number)]
            issue, _ = load_issue(MANIFEST, number)
            with self.subTest(issue=number):
                self.assertEqual(frozen, issue["frozen_test_revision"])
                actual = subprocess.check_output([
                    "git", "-C", str(ROOT), "rev-parse", f"{frozen}:{case['source']}"], text=True).strip()
                self.assertEqual(case["source_blob"], actual)
                expected_blobs = {case["source"]: actual}
                if number == 2871:
                    from source_context import DECLARATION
                    expected_blobs.update(DECLARATION["fixture_blobs"])
                self.assertEqual(expected_blobs, issue["frozen_test_blobs"])
                self.assertEqual(case["paths"], issue["allowed_production_paths"])
                self.assertEqual(case["required_environments"], issue["required_environments"])
                self.assertEqual(f"FullyQualifiedName~Issue{number}_Tests", issue["filter"])
                for path in case["paths"]:
                    subprocess.check_call(["git", "-C", str(ROOT), "cat-file", "-e", f"{frozen}:{path}"])

    def test_exact_observed_baselines_and_all_green_candidates(self):
        for number, (failures, controls) in COUNTS.items():
            issue, _ = load_issue(MANIFEST, number)
            with self.subTest(issue=number):
                self.assertEqual(failures, len(issue["regressions"]))
                self.assertEqual(controls, len(issue["controls"]))
                verify_focused(observed_run(number, True), issue, baseline=True)
                verify_focused(observed_run(number, False), issue, baseline=False)

    def test_every_regression_rejects_wrong_red_or_unexpected_baseline_pass(self):
        for number in COUNTS:
            issue, _ = load_issue(MANIFEST, number)
            for case in issue["regressions"]:
                for outcome, message in (("Failed", "System.TimeoutException"), ("Passed", "")):
                    with self.subTest(issue=number, case=case["name"], outcome=outcome):
                        run = observed_run(number, True)
                        run.tests[case["name"]] = replace(run.tests[case["name"]],
                                                         outcome=outcome, message=message)
                        with self.assertRaises(GateError):
                            verify_focused(run, issue, baseline=True)

    def test_no_missing_extra_or_skipped_candidate_case(self):
        for number in COUNTS:
            issue, _ = load_issue(MANIFEST, number)
            for name in observed_run(number, False).tests:
                for mutation in ("missing", "extra", "skip"):
                    with self.subTest(issue=number, case=name, mutation=mutation):
                        run = observed_run(number, False)
                        if mutation == "missing":
                            del run.tests[name]
                        elif mutation == "extra":
                            run.tests[name + "_extra"] = replace(run.tests[name], name=name + "_extra")
                        else:
                            run.tests[name] = replace(run.tests[name], outcome="NotExecuted")
                        with self.assertRaises(GateError):
                            verify_focused(run, issue, baseline=False)

    def test_every_control_must_pass_before_and_after_fix(self):
        for number in COUNTS:
            issue, _ = load_issue(MANIFEST, number)
            for case in issue["controls"]:
                for baseline in (True, False):
                    with self.subTest(issue=number, case=case["name"], baseline=baseline):
                        run = observed_run(number, baseline)
                        run.tests[case["name"]] = replace(run.tests[case["name"]],
                                                         outcome="Failed", message="Control failed")
                        with self.assertRaisesRegex(GateError, "Expected Passed"):
                            verify_focused(run, issue, baseline=baseline)

    def test_closure_and_numeric_failure_details_cannot_be_normalized_away(self):
        for number in (2779, 2845):
            issue, _ = load_issue(MANIFEST, number)
            run = observed_run(number, True)
            mutations = 0
            for name, case in run.tests.items():
                if case.outcome != "Failed":
                    continue
                changed = case.message.replace("DisplayClass6_0", "DisplayClass1_0")
                changed = changed.replace("to be 1L", "to be 0L")
                if changed != case.message:
                    mutations += 1
                    with self.subTest(issue=number, case=name):
                        candidate = observed_run(number, True)
                        candidate.tests[name] = replace(case, message=changed)
                        with self.assertRaisesRegex(GateError, "expected defect"):
                            verify_focused(candidate, issue, baseline=True)
            self.assertGreater(mutations, 0, f"Issue {number} must exercise a changed detail")


if __name__ == "__main__":
    unittest.main()
