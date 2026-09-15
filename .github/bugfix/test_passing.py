"""Previously accepted contracts cannot be converted into known failures again."""

import copy
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, patch

from passing import assert_passing, load_snapshot, passing_cases, selection_filter
from state import Rejected, apply_event, new_state


class PassingContractTests(unittest.TestCase):
    def setUp(self):
        self.names = ["LiteDB.Tests.Issue1000_Tests.Valid_input", "LiteDB.Tests.Issue1000_Tests.Window(value: 1)"]
        self.ledger = {"schema_version": 1, "issues": {"1000": {"candidate_sha": "a" * 40,
                       "test_source_sha": "b" * 40, "tests": self.names}}}

    def test_accepted_candidate_must_be_ancestor_of_selected_base(self):
        ancestor = Mock(return_value=True)
        self.assertEqual(self.names, passing_cases(self.ledger, "c" * 40, "b" * 40, ancestor))
        ancestor.assert_called_once_with("a" * 40, "c" * 40)
        with self.assertRaisesRegex(Rejected, "not an ancestor"):
            passing_cases(self.ledger, "c" * 40, "b" * 40, lambda *args: False)

    def test_baseline_and_candidate_must_keep_every_accepted_case_passing(self):
        for variant in ("baseline", "candidate"):
            for bad in ("Failed", "NotExecuted", "missing"):
                with self.subTest(variant=variant, outcome=bad):
                    cases = {"opaque1": SimpleNamespace(name=self.names[0], outcome="Passed")}
                    if bad != "missing":
                        cases["opaque2"] = SimpleNamespace(name=self.names[1], outcome=bad)
                    with self.assertRaisesRegex(Rejected, "Previously accepted tests regressed"):
                        assert_passing(SimpleNamespace(tests=cases), self.names, variant)

    def test_all_occurrences_of_an_accepted_display_name_must_pass(self):
        result = SimpleNamespace(tests={"opaque1": SimpleNamespace(name=self.names[0], outcome="Passed"),
                                        "opaque2": SimpleNamespace(name=self.names[0], outcome="Failed")})
        with self.assertRaises(Rejected):
            assert_passing(result, self.names[:1], "candidate")

    def test_exact_method_filter_keeps_current_issue_contract_separate(self):
        selected = selection_filter(self.names)
        self.assertEqual("FullyQualifiedName=LiteDB.Tests.Issue1000_Tests.Valid_input|"
                         "FullyQualifiedName=LiteDB.Tests.Issue1000_Tests.Window", selected)
        with self.assertRaises(Rejected):
            selection_filter(["LiteDB.Tests.Method|FullyQualifiedName~Anything"])

    def test_snapshot_binds_exact_data_commit_and_ledger(self):
        import json
        responses = ["", "d" * 40, "100644 blob ignored\taccepted-tests.json", json.dumps(self.ledger), "a" * 40]
        with patch("passing.git", side_effect=responses):
            snapshot, tests = load_snapshot("repo", "owner/repo", "d" * 40, "c" * 40, "b" * 40)
        self.assertEqual("d" * 40, snapshot["state_commit"])
        self.assertEqual(2, snapshot["case_count"])
        self.assertEqual(self.names, tests)
        self.assertEqual(64, len(snapshot["ledger_sha256"]))

    def test_snapshot_cannot_be_removed_or_changed_by_an_event(self):
        state = new_state("canary", 1234, "a" * 40, "b" * 40, "c" * 40)
        state["passing_contract"] = {"state_commit": "d" * 40, "ledger_sha256": "e" * 64}
        event = {key: state[key] for key in ("campaign", "issue", "base_sha", "test_source_sha", "workflow_sha")}
        event.update(schema_version=1, kind="pause", event_id="pause-1")
        original = copy.deepcopy(state)
        with self.assertRaisesRegex(Rejected, "passing-contract"):
            apply_event(state, event)
        event["passing_contract"] = {"state_commit": "f" * 40}
        with self.assertRaises(Rejected):
            apply_event(state, event)
        self.assertEqual(original, state)


if __name__ == "__main__":
    unittest.main()
