"""UInt64 co-repair keeps the prior widening contract permanently green."""

import json
from pathlib import Path
from types import SimpleNamespace
import unittest

from passing import assert_passing, passing_cases
from state import Rejected


class M111PermanentPassingTests(unittest.TestCase):
    def test_all_six_2869_cases_stay_required_before_and_after_uint64_fix(self):
        root = Path(__file__).resolve().parents[2]
        manifest = json.loads((root / "scripts/bugfix/issues.json").read_text(encoding="utf-8"))
        contract = manifest["issues"]["2869"]
        names = [case["name"] for case in contract["regressions"] + contract["controls"]]
        self.assertEqual(6, len(names))
        ledger = {"schema_version": 1, "issues": {"2869": {"candidate_sha": "a" * 40,
                  "test_source_sha": contract["frozen_test_revision"], "tests": names}}}
        required = passing_cases(ledger, "b" * 40, contract["frozen_test_revision"], lambda *args: True)
        for variant in ("baseline", "candidate"):
            for name in names:
                for outcome in ("Failed", "NotExecuted", "missing"):
                    cases = {n: SimpleNamespace(name=n, outcome="Passed") for n in names}
                    if outcome == "missing":
                        del cases[name]
                    else:
                        cases[name].outcome = outcome
                    with self.subTest(variant=variant, name=name, outcome=outcome), self.assertRaises(Rejected):
                        assert_passing(SimpleNamespace(tests=cases), required, variant)


if __name__ == "__main__":
    unittest.main()
