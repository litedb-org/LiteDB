"""The metadata repair cannot change candidates or arbitrary test identities."""

import copy
import unittest

from repair_ledger_encoding import corrected_records
from state import Rejected


class RepairEncodingTests(unittest.TestCase):
    def fixture(self):
        correct = "LiteDB.Tests.Case(bytes: [0, ···])"
        entry = {"campaign": "canary", "candidate_sha": "a" * 40,
                 "tests": [correct.encode("utf-8").decode("cp1252")], "proof": "retained"}
        state = {"phase": "integrated", "issue": 2874, "campaign": "canary", "candidate_sha": "a" * 40,
                 "test_source_sha": "b" * 40, "accepted_tests": copy.deepcopy(entry)}
        ledger = {"issues": {"2874": entry}}
        contract = {"inventory_issue": 2874, "frozen_test_revision": "b" * 40,
                    "regressions": [{"name": correct}], "controls": []}
        return state, ledger, contract

    def test_only_name_fields_change_and_inputs_stay_immutable(self):
        state, ledger, contract = self.fixture()
        old = copy.deepcopy((state, ledger))
        fixed_state, fixed_ledger, audit = corrected_records(state, ledger, contract)
        self.assertEqual((state, ledger), old)
        fixed_state["accepted_tests"]["tests"] = audit["before"]
        fixed_ledger["issues"]["2874"]["tests"] = audit["before"]
        self.assertEqual((fixed_state, fixed_ledger), old)

    def test_rejects_arbitrary_or_already_correct_names(self):
        for names in (["LiteDB.Tests.Unrelated"], ["LiteDB.Tests.Case(bytes: [0, ···])"]):
            state, ledger, contract = self.fixture()
            state["accepted_tests"]["tests"] = names
            ledger["issues"]["2874"]["tests"] = names
            with self.assertRaises(Rejected):
                corrected_records(state, ledger, contract)

    def test_rejects_changed_candidate_or_source(self):
        for field in ("candidate_sha", "test_source_sha", "phase"):
            state, ledger, contract = self.fixture()
            state[field] = "changed"
            with self.assertRaises(Rejected):
                corrected_records(state, ledger, contract)


if __name__ == "__main__":
    unittest.main()
