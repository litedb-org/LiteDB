"""Acceptance contracts for the two canaries queued after issue 2874."""

from dataclasses import replace
from pathlib import Path
import unittest

from policy import load_issue, verify_focused
from trx import GateError, TestResult, TestRun


MANIFEST = Path(__file__).with_name("issues.json")
CASES = {
    2839: {
        "failure": "System.OverflowException : Value was either too large or too small for an Int32.",
        "regressions": ["LongCount_Query_routes_the_exact_query_and_preserves_Int64_width"],
        "controls": ["Count_Query_routes_the_exact_query_and_reads_an_Int32_result",
                     "LongCount_without_Query_preserves_an_engine_result_above_Int32"],
    },
    2869: {
        "failure": "System.InvalidCastException : Unable to cast object of type 'System.Int32' to type 'System.Int64'.",
        "regressions": [
            "Int32_values_implicitly_widen_to_Int64_and_Double(expected: -2147483648)",
            "Int32_values_implicitly_widen_to_Int64_and_Double(expected: -1)",
            "Int32_values_implicitly_widen_to_Int64_and_Double(expected: 0)",
            "Int32_values_implicitly_widen_to_Int64_and_Double(expected: 1)",
            "Int32_values_implicitly_widen_to_Int64_and_Double(expected: 2147483647)",
        ],
        "controls": ["Native_numeric_conversions_work_and_non_numeric_values_are_not_coerced"],
    },
}


def observed_run(number, baseline):
    """Model exact names/outcomes observed in the clean dd937719 net10 run."""
    case = CASES[number]
    class_name = f"LiteDB.Tests.Issues.Issue{number}_Tests"
    tests = {}
    for method in case["regressions"] + case["controls"]:
        failed = baseline and method in case["regressions"]
        name = class_name + "." + method
        tests[name] = TestResult(name, "Failed" if failed else "Passed",
                                 case["failure"] if failed else "", class_name)
    return TestRun(tests, "c" * 64)


class NextIssueContracts(unittest.TestCase):
    def test_observed_baseline_and_correct_candidate(self):
        for number in CASES:
            with self.subTest(issue=number):
                issue, _ = load_issue(MANIFEST, number)
                verify_focused(observed_run(number, True), issue, baseline=True)
                verify_focused(observed_run(number, False), issue, baseline=False)

    def test_unrelated_crash_cannot_establish_expected_red(self):
        for number in CASES:
            with self.subTest(issue=number):
                issue, _ = load_issue(MANIFEST, number)
                run = observed_run(number, True)
                name = next(iter(run.tests))
                run.tests[name] = replace(run.tests[name], message="System.OutOfMemoryException")
                with self.assertRaisesRegex(GateError, "expected defect"):
                    verify_focused(run, issue, baseline=True)

    def test_correct_target_does_not_excuse_a_broken_control(self):
        for number in CASES:
            with self.subTest(issue=number):
                issue, _ = load_issue(MANIFEST, number)
                run = observed_run(number, False)
                name = issue["controls"][0]["name"]
                run.tests[name] = replace(run.tests[name], outcome="Failed", message="Control failed")
                with self.assertRaisesRegex(GateError, "Expected Passed"):
                    verify_focused(run, issue, baseline=False)

    def test_boundary_case_cannot_disappear_from_numeric_selection(self):
        issue, _ = load_issue(MANIFEST, 2869)
        run = observed_run(2869, False)
        del run.tests[issue["regressions"][0]["name"]]
        with self.assertRaisesRegex(GateError, "selection changed"):
            verify_focused(run, issue, baseline=False)


if __name__ == "__main__":
    unittest.main()
