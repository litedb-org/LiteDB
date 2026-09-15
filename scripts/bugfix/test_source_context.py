"""A real immutable Git context change is observable without proving behavior."""

import copy
from collections import Counter
from dataclasses import replace
import json
from pathlib import Path
import subprocess
import tempfile
import unittest

from policy import compare_ledger, make_ledger
from source_context import CLASS, DECLARATION, FROZEN, TEST, final_observations, observe
from trx import GateError, TestResult, TestRun


class SourceContextTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temporary = tempfile.TemporaryDirectory(prefix="litedb-source-observation-tests-")
        cls.repository = Path(cls.temporary.name) / "source"
        root = Path(__file__).resolve().parents[2]
        subprocess.run(["git", "clone", "--shared", "--no-checkout", "--quiet", str(root), str(cls.repository)], check=True)
        cls.contract = json.loads((root / "scripts/bugfix/issues.json").read_bytes())["issues"]["2871"]
        cls.source = cls.git("show", FROZEN + ":" + DECLARATION["source_path"])
        # Insert a comment, preserving the actual unsafe implementation. This must
        # remain a source-only observation, never a claim of corrected behavior.
        cls.changed = cls.source.replace(b"                if (_cacheCtor.TryGetValue", b"                // Context-only test fixture.\n                if (_cacheCtor.TryGetValue", 1)
        cls.candidate = cls.commit({DECLARATION["source_path"]: cls.changed})
        cls.observation = observe(cls.repository, cls.contract, FROZEN, cls.candidate)[0]

    @classmethod
    def tearDownClass(cls):
        cls.temporary.cleanup()

    @classmethod
    def git(cls, *args, data=None):
        return subprocess.run(["git", "-C", str(cls.repository), *args], input=data, capture_output=True, check=True).stdout

    @classmethod
    def commit(cls, files):
        cls.git("read-tree", FROZEN)
        for path, raw in files.items():
            sha = cls.git("hash-object", "-w", "--stdin", data=raw).decode().strip()
            cls.git("update-index", "--cacheinfo", "100644", sha, path)
        tree = cls.git("write-tree").decode().strip()
        return cls.git("-c", "user.name=Observation test", "-c", "user.email=test@example.invalid",
                       "commit-tree", tree, "-p", FROZEN, "-m", "Source-only observation fixture; no behavior fix").decode().strip()

    def comparison(self, extra_behavior_pass=False):
        baseline, candidate = {}, {}
        for case in self.contract["regressions"] + self.contract["controls"]:
            name = case["name"]
            baseline[name] = TestResult(name, "Failed" if "failure_first_line" in case else "Passed",
                                        case.get("failure_first_line", ""), "LiteDB.Tests.Issues.Issue2871_Tests", name)
            candidate[name] = replace(baseline[name], outcome="Passed", message="")
        baseline[TEST] = TestResult(TEST, "Failed", self.observation["baseline_failure"], CLASS, "guard132")
        candidate[TEST] = replace(baseline[TEST], outcome="Passed", message="")
        if extra_behavior_pass:
            name = "LiteDB.Tests.Audit2026.ExpressionAuditRegression_Tests.M111_uint64_round_trips_through_bson_value"
            baseline[name] = TestResult(name, "Failed", "InvalidCastException", "LiteDB.Tests.Audit2026.ExpressionAuditRegression_Tests", "behavior")
            candidate[name] = replace(baseline[name], outcome="Passed", message="")
        definitions = {test.test_id: test.name for test in baseline.values()}
        provenance = {"issue": 2871, "base_sha": FROZEN, "candidate_sha": self.candidate,
                      "test_definition_sha": FROZEN, "environment": "linux-x64-net8.0", "manifest_sha256": "a" * 64,
                      "failure_normalization_sha256": "b" * 64}
        before = TestRun(baseline, "c" * 64, definitions)
        after = TestRun(candidate, "d" * 64, definitions.copy())
        ledger = make_ledger(before, provenance, {test.class_name for test in baseline.values()},
                             Counter(definitions.values()), set(), {})
        return ledger, after, provenance

    def compare(self, ledger, candidate, provenance):
        return compare_ledger(ledger, candidate, self.contract, provenance, Counter(candidate.definitions.values()), {}, [self.observation])

    def test_actual_comment_only_context_pair_has_no_behavioral_credit(self):
        self.assertTrue(self.observation["behavior_unverified"])
        self.assertFalse(self.observation["issue_credit"])
        self.assertEqual([], observe(self.repository, self.contract, FROZEN, FROZEN))
        report = self.compare(*self.comparison())
        self.assertTrue(report["accepted"])
        self.assertEqual([self.observation], report["source_context_changes"])
        self.assertNotIn(TEST, [case["name"] for case in self.contract["regressions"] + self.contract["controls"]])

    def test_wrong_guard_path_source_blob_fixture_or_context_is_rejected(self):
        for key, value in (("guard_id", 111), ("source_path", "LiteDB/Document/BsonValue.cs"),
                           ("frozen_source_blob", "0" * 40), ("context_sha256", "0" * 64), ("fixture_blobs", {})):
            contract = copy.deepcopy(self.contract)
            contract["source_context_observations"][0][key] = value
            with self.subTest(key=key), self.assertRaisesRegex(ValueError, "Unapproved"):
                observe(self.repository, contract, FROZEN, self.candidate)
        fixture = next(iter(DECLARATION["fixture_blobs"]))
        candidate = self.commit({DECLARATION["source_path"]: self.changed, fixture: self.git("show", FROZEN + ":" + fixture) + b"\n"})
        with self.assertRaisesRegex(ValueError, "fixture changed"):
            observe(self.repository, self.contract, FROZEN, candidate)

    def test_unapproved_production_diff_and_behavioral_guard_target_are_rejected(self):
        other = "LiteDB/Document/BsonValue.cs"
        candidate = self.commit({DECLARATION["source_path"]: self.changed, other: self.git("show", FROZEN + ":" + other) + b"\n"})
        with self.assertRaisesRegex(ValueError, "exact approved diff"):
            observe(self.repository, self.contract, FROZEN, candidate)
        contract = copy.deepcopy(self.contract)
        contract["controls"].append({"name": TEST})
        with self.assertRaisesRegex(ValueError, "behavioral issue credit"):
            observe(self.repository, contract, FROZEN, self.candidate)

    def test_behavioral_m111_pass_missing_and_skipped_guard_still_block(self):
        report = self.compare(*self.comparison(extra_behavior_pass=True))
        self.assertFalse(report["accepted"])
        self.assertIn("M111_uint64", report["unexpected_passes"][0])
        ledger, candidate, provenance = self.comparison()
        candidate.tests[TEST] = replace(candidate.tests[TEST], outcome="NotExecuted")
        self.assertFalse(self.compare(ledger, candidate, provenance)["accepted"])
        del candidate.tests[TEST]
        with self.assertRaises(GateError):
            self.compare(ledger, candidate, provenance)

    def test_final_observation_requires_the_same_recorded_disposition(self):
        entry = {"base_sha": FROZEN, "candidate_sha": self.candidate, "source_context_changes": [self.observation]}
        accepted = {2871: {"entry": entry, "contract": self.contract}}
        final = final_observations(self.repository, accepted, self.candidate)
        self.assertEqual(self.observation["candidate_source_blob"], final[0]["final_source_blob"])
        self.assertFalse(final[0]["issue_credit"])
        with self.assertRaisesRegex(ValueError, "context returned"):
            final_observations(self.repository, accepted, FROZEN)
        entry.pop("source_context_changes")
        with self.assertRaisesRegex(ValueError, "missing"):
            final_observations(self.repository, accepted, self.candidate)


if __name__ == "__main__":
    unittest.main()
