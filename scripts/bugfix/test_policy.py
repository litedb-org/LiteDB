import copy
from collections import Counter
from dataclasses import replace
import unittest

from policy import compare_ledger, make_ledger, require_sha, verify_focused
from test_support import ISSUE, PROVENANCE, add_test, target_run
from trx import GateError


class FocusedTests(unittest.TestCase):
    def test_expected_red_then_green(self):
        verify_focused(target_run(), ISSUE, True)
        verify_focused(target_run(False), ISSUE, False)

    def test_unrelated_exception_is_not_expected_red(self):
        run = target_run()
        name = next(iter(run.tests))
        run.tests[name] = replace(run.tests[name], message="ObjectDisposedException")
        with self.assertRaisesRegex(GateError, "expected defect"):
            verify_focused(run, ISSUE, True)

    def test_failed_controls_reject_blanket_argument_guard(self):
        run = target_run(False)
        name = ISSUE["controls"][0]["name"]
        run.tests[name] = replace(run.tests[name], outcome="Failed", message="ArgumentException")
        with self.assertRaisesRegex(GateError, "Expected Passed"):
            verify_focused(run, ISSUE, False)

    def test_missing_added_and_skipped_cases_rejected(self):
        for change in ("missing", "added", "skipped"):
            run = target_run(False)
            name = next(iter(run.tests))
            if change == "missing":
                del run.tests[name]
            elif change == "added":
                add_test(run, "Extra", "Passed")
            else:
                run.tests[name] = replace(run.tests[name], outcome="NotExecuted")
            with self.subTest(change=change), self.assertRaises(GateError):
                verify_focused(run, ISSUE, False)

    def test_short_or_option_like_commit_identity_rejected(self):
        for value in ("abc123", "--all", "A" * 40, "a" * 39):
            with self.subTest(value=value), self.assertRaises(GateError):
                require_sha(value)


class LedgerTests(unittest.TestCase):
    def setUp(self):
        self.baseline = target_run()
        self.failed = add_test(self.baseline, "KnownDefect", "Failed", "Expected 4, found 3\n   at Test in /base:line 8")
        self.passed = add_test(self.baseline, "ExistingControl", "Passed")
        self.skipped = add_test(self.baseline, "ExistingSkip", "NotExecuted")
        self.ledger = make_ledger(self.baseline, PROVENANCE,
                                  {test.class_name for test in self.baseline.tests.values()},
                                  Counter(self.baseline.definitions.values()), {self.skipped}, {})
        self.candidate = target_run(False)
        for name in (self.failed, self.passed, self.skipped):
            self.candidate.tests[name] = self.baseline.tests[name]
            test_id = self.baseline.tests[name].test_id
            self.candidate.definitions[test_id] = self.baseline.definitions[test_id]

    def compare(self):
        return compare_ledger(self.ledger, self.candidate, ISSUE, PROVENANCE,
                              Counter(self.baseline.definitions.values()), {})

    def test_only_identical_known_failures_and_existing_skips_allowed(self):
        result = self.compare()
        self.assertTrue(result["accepted"])
        self.assertEqual([self.failed], result["known_failures"])

    def test_new_failure_cannot_replace_fixed_failure_at_same_count(self):
        self.candidate.tests[self.failed] = replace(self.candidate.tests[self.failed], outcome="Passed", message="")
        self.candidate.tests[self.passed] = replace(self.candidate.tests[self.passed], outcome="Failed", message="New bug")
        result = self.compare()
        self.assertFalse(result["accepted"])
        self.assertEqual([self.failed], result["unexpected_passes"])
        self.assertTrue(result["errors"])

    def test_unexpected_pass_requires_explicit_disposition(self):
        self.candidate.tests[self.failed] = replace(self.candidate.tests[self.failed], outcome="Passed", message="")
        self.assertFalse(self.compare()["accepted"])
        self.assertEqual([self.failed], self.compare()["unexpected_passes"])

    def test_changed_known_failure_rejected(self):
        self.candidate.tests[self.failed] = replace(self.candidate.tests[self.failed], message="Expected 4, found 0")
        self.assertFalse(self.compare()["accepted"])

    def test_reviewed_volatile_value_matches_but_assertion_change_rejects(self):
        normalization = {self.failed: [{"pattern": "(?<=found )[0-9]+",
                                        "replacement": "<observed>", "matches": 1}]}
        ledger = make_ledger(
            self.baseline, PROVENANCE,
            {test.class_name for test in self.baseline.tests.values()},
            Counter(self.baseline.definitions.values()), {self.skipped}, normalization)
        self.candidate.tests[self.failed] = replace(
            self.candidate.tests[self.failed], message="Expected 4, found 99")
        result = compare_ledger(ledger, self.candidate, ISSUE, PROVENANCE,
                                Counter(self.baseline.definitions.values()), normalization)
        self.assertTrue(result["accepted"])

        self.candidate.tests[self.failed] = replace(
            self.candidate.tests[self.failed], message="Expected 5, found 99")
        result = compare_ledger(ledger, self.candidate, ISSUE, PROVENANCE,
                                Counter(self.baseline.definitions.values()), normalization)
        self.assertFalse(result["accepted"])

    def test_pass_to_failed_remains_an_outcome_change(self):
        self.candidate.tests[self.passed] = replace(
            self.candidate.tests[self.passed], outcome="Failed",
            message="Expected peak <= budget, but found 999")
        result = self.compare()
        self.assertFalse(result["accepted"])
        self.assertTrue(any("Outcome changed Passed -> Failed" in error
                            for error in result["errors"]))

    def test_reviewed_intermittent_pass_failure_flip_is_machine_readable(self):
        intermittent_class = self.baseline.tests[self.passed].class_name
        ledger = make_ledger(
            self.baseline, PROVENANCE,
            {test.class_name for test in self.baseline.tests.values()},
            Counter(self.baseline.definitions.values()), {self.skipped}, {},
            {intermittent_class})
        self.candidate.tests[self.passed] = replace(
            self.candidate.tests[self.passed], outcome="Failed", message="volatile peak")
        result = compare_ledger(ledger, self.candidate, ISSUE, PROVENANCE,
                                Counter(self.baseline.definitions.values()), {})
        self.assertFalse(result["accepted"])
        self.assertEqual([], result["errors"])
        self.assertEqual(
            [{"name": self.passed, "class_name": intermittent_class,
              "baseline_outcome": "Passed", "candidate_outcome": "Failed"}],
            result["inconclusive_changes"])

    def test_only_stack_location_changes_are_ignored(self):
        self.candidate.tests[self.failed] = replace(self.candidate.tests[self.failed], message="Expected 4, found 3\n   at Test in /candidate:line 10")
        self.assertTrue(self.compare()["accepted"])

    def test_new_skips_rejected(self):
        self.candidate.tests[self.passed] = replace(self.candidate.tests[self.passed], outcome="NotExecuted")
        self.assertFalse(self.compare()["accepted"])

    def test_missing_baseline_test_rejected(self):
        del self.candidate.tests[self.skipped]
        with self.assertRaisesRegex(GateError, "completed results"):
            self.compare()

    def test_extra_deferred_theory_execution_rejected(self):
        source = self.candidate.tests[self.passed]
        self.candidate.tests["extra-case-key"] = replace(source, execution_id="random-run-id")
        with self.assertRaisesRegex(GateError, "result instances changed"):
            self.compare()

    def test_truncated_baseline_cannot_define_its_own_inventory(self):
        expected = Counter(self.baseline.definitions.values())
        removed = next(iter(self.baseline.tests))
        test_id = self.baseline.tests[removed].test_id
        del self.baseline.tests[removed]
        del self.baseline.definitions[test_id]
        with self.assertRaisesRegex(GateError, "inventory mismatch"):
            make_ledger(self.baseline, PROVENANCE,
                        {test.class_name for test in self.baseline.tests.values()},
                        expected, {self.skipped}, {})

    def test_candidate_must_match_independent_inventory(self):
        add_test(self.candidate, "UnexpectedDiscovery", "Passed")
        with self.assertRaisesRegex(GateError, "inventory mismatch"):
            self.compare()

    def test_stale_ledger_rejected(self):
        for key in PROVENANCE:
            ledger = copy.deepcopy(self.ledger)
            ledger["provenance"] = {**PROVENANCE, key: "wrong"}
            with self.subTest(key=key), self.assertRaisesRegex(GateError, "provenance"):
                compare_ledger(ledger, self.candidate, ISSUE, PROVENANCE,
                               Counter(self.baseline.definitions.values()), {})

    def test_unclassified_baseline_failure_rejected(self):
        with self.assertRaisesRegex(GateError, "Unclassified"):
            make_ledger(self.baseline, PROVENANCE, set(),
                        Counter(self.baseline.definitions.values()),
                        {self.skipped}, {})

    def test_unclassified_baseline_skip_rejected(self):
        with self.assertRaisesRegex(GateError, "Unclassified baseline skip"):
            make_ledger(self.baseline, PROVENANCE,
                        {test.class_name for test in self.baseline.tests.values()},
                        Counter(self.baseline.definitions.values()), set(), {})


if __name__ == "__main__":
    unittest.main()
