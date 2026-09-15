"""Pin the next wave to independently observed original regression identities."""

from dataclasses import replace
import json
from pathlib import Path
import subprocess
import unittest

from policy import load_issue, verify_focused
from trx import GateError, TestResult, TestRun


ROOT = Path(__file__).resolve().parents[2]
MANIFEST = ROOT / "scripts/bugfix/issues.json"
FROZEN = "dd937719f7eee53c512f50ac604cab639bf42a4c"
CASES = {
    1506: {
        "blob": "42722f965457a84ac385399b59707e1a7d9a9084",
        "path": "LiteDB/Client/Database/Collections/Find.cs",
        "failures": {
            "Find_paging_override_preserves_the_complete_reusable_query":
                "Expected query.Offset to be 1 because immediately after Find returns, but found 2.",
        },
        "control": "Find_honors_query_limit_when_optional_paging_is_omitted",
    },
    1002: {
        "blob": "300112d2fa9c10055460484549e551f7d750451e",
        "path": "LiteDB/Client/Database/Collections/Insert.cs",
        "failures": {
            "Nullable_integer_empty_ids_generate_unique_persisted_integer_keys(empty: null)":
                "LiteDB.LiteException : Invalid BSON data type 'Null' on field '_id'.",
        },
        "control": "Nullable_integer_empty_ids_generate_unique_persisted_integer_keys(empty: 0)",
    },
    2802: {
        "blob": "d39406d4e8982b3081fb58149848907a25d97822",
        "path": "LiteDB/Client/Database/LiteQueryable.cs",
        "failures": {},
        "control": "Generic_only_mapper_control_differs_from_default_mapping",
    },
}
for api in ("Find", "FindAll", "FindById", "FindOne", "Query"):
    CASES[2802]["failures"][
        f'Typed_read_uses_virtual_mapper_and_returns_its_result(api: "{api}")'
    ] = ('Expected result.Name to be "decoded:one" with a length of 11 because calling and '
         'discarding the override\'s result is insufficient, but "one" has a length of 3, '
         'differs near "one" (index 0).')
    CASES[2802]["failures"][
        f'Typed_read_uses_generic_only_virtual_mapper_and_returns_its_result(api: "{api}")'
    ] = ('Expected result.Name to be "generic:stored" with a length of 14, but "stored" '
         'has a length of 6, differs near "sto" (index 0).')


def observed_run(number, baseline):
    """Exact observations from run 34988724926, identical in all six baseline lanes."""
    case = CASES[number]
    class_name = f"LiteDB.Tests.Issues.Issue{number}_Tests"
    tests = {}
    for method in list(case["failures"]) + [case["control"]]:
        failed = baseline and method in case["failures"]
        name = class_name + "." + method
        tests[name] = TestResult(name, "Failed" if failed else "Passed",
                                case["failures"][method] if failed else "", class_name)
    return TestRun(tests, "c" * 64)


class NextWaveContracts(unittest.TestCase):
    def test_existing_three_contracts_remain_exactly_unchanged(self):
        original = json.loads(subprocess.check_output(
            ["git", "-C", str(ROOT), "show", "8b02fdaeecbcbb6c0df72aab5abcd9777eb90bfd"]))
        current = json.loads(MANIFEST.read_text(encoding="utf-8"))
        for number, contract in original["issues"].items():
            with self.subTest(issue=number):
                self.assertEqual(contract, current["issues"][number])

    def test_frozen_blobs_and_narrow_paths_match_independent_contracts(self):
        for number, case in CASES.items():
            with self.subTest(issue=number):
                issue, _ = load_issue(MANIFEST, number)
                source = f"LiteDB.Tests/Issues/Issue{number}_Tests.cs"
                self.assertEqual(FROZEN, issue["frozen_test_revision"])
                self.assertEqual({source: case["blob"]}, issue["frozen_test_blobs"])
                actual = subprocess.check_output(
                    ["git", "-C", str(ROOT), "rev-parse", f"{FROZEN}:{source}"], text=True).strip()
                self.assertEqual(case["blob"], actual)
                self.assertEqual([case["path"]], issue["allowed_production_paths"])
                self.assertEqual(f"FullyQualifiedName~Issue{number}_Tests", issue["filter"])

    def test_all_observed_baselines_and_complete_green_candidates(self):
        for number in CASES:
            with self.subTest(issue=number):
                issue, _ = load_issue(MANIFEST, number)
                verify_focused(observed_run(number, True), issue, baseline=True)
                verify_focused(observed_run(number, False), issue, baseline=False)

    def test_each_regression_requires_its_own_exact_failure(self):
        for number in CASES:
            issue, _ = load_issue(MANIFEST, number)
            for case in issue["regressions"]:
                with self.subTest(issue=number, case=case["name"]):
                    run = observed_run(number, True)
                    run.tests[case["name"]] = replace(run.tests[case["name"]],
                                                     message="System.OutOfMemoryException")
                    with self.assertRaisesRegex(GateError, "expected defect"):
                        verify_focused(run, issue, baseline=True)

    def test_no_missing_extra_or_skipped_candidate_cases(self):
        for number in CASES:
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

    def test_control_must_pass_before_and_after_fix(self):
        for number in CASES:
            issue, _ = load_issue(MANIFEST, number)
            for baseline in (True, False):
                with self.subTest(issue=number, baseline=baseline):
                    run = observed_run(number, baseline)
                    name = issue["controls"][0]["name"]
                    run.tests[name] = replace(run.tests[name], outcome="Failed", message="Control failed")
                    with self.assertRaisesRegex(GateError, "Expected Passed"):
                        verify_focused(run, issue, baseline=baseline)


if __name__ == "__main__":
    unittest.main()
