"""Immutable selected-lane and production-proof contracts."""

import hashlib
import json
import unittest
from types import SimpleNamespace
from unittest.mock import patch

from profiles import production_evidence, targeted_coverage, validate_profile
from state import Rejected
from test_artifacts import archive


def profile_fixture(candidate="d" * 40, base="a" * 40, issue=2874):
    profile = {"schema_version": 1, "issue": issue, "base_sha": base, "candidate_sha": candidate,
               "required_lanes": ["bugfix-check-ubuntu-latest-net8.0"],
               "matrix": [{"os": "ubuntu-latest", "framework": "net8.0"}],
               "production_build": True, "compatibility": False, "required_environments": [],
               "targeted_test_filters": [], "policy_sha256": "1" * 64}
    raw = json.dumps(profile, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode()
    profile["profile_sha256"] = hashlib.sha256(raw).hexdigest()
    return profile


class ProfileBindingTests(unittest.TestCase):
    def test_required_targeted_coverage_rejects_missing_or_all_skipped_cases(self):
        selection = "FullyQualifiedName~LiteDB.Tests.Upgrade"
        run = SimpleNamespace(tests={"case": SimpleNamespace(name="LiteDB.Tests.Upgrade.Roundtrip", outcome="Passed")})
        self.assertEqual(1, targeted_coverage(run, [selection])[selection]["executed"])
        run.tests["case"].outcome = "NotExecuted"
        with self.assertRaisesRegex(Rejected, "missing or all skipped"):
            targeted_coverage(run, [selection])
        run.tests.clear()
        with self.assertRaisesRegex(Rejected, "missing or all skipped"):
            targeted_coverage(run, [selection])

    def test_changing_candidate_or_dropping_build_invalidates_profile(self):
        state = {"issue": 2874, "base_sha": "a" * 40, "candidate_sha": "d" * 40}
        profile = profile_fixture()
        validate_profile(profile, state)
        with self.assertRaises(Rejected):
            validate_profile(profile, {**state, "candidate_sha": "e" * 40})
        profile["production_build"] = False
        with self.assertRaises(Rejected):
            validate_profile(profile, state)


class ProductionEvidenceTests(unittest.TestCase):
    def setUp(self):
        self.state = {"issue": 2874, "base_sha": "a" * 40, "candidate_sha": "d" * 40,
                      "acceptance_profile": profile_fixture()}
        self.report = {"schema_version": 1, **self.state, "workflow_sha": "c" * 40,
                       "accepted": True, "production_build": True, "compatibility": False}
        self.job = {"name": "production", "status": "completed", "conclusion": "success"}

    def verify(self):
        def api(repo, path):
            if "/jobs" in path:
                return {"total_count": 1, "jobs": [self.job]}
            return {"total_count": 1, "artifacts": [{"id": 90, "name": "bugfix-production", "expired": False}]}
        data = archive({"production.json": self.report})
        with patch("storage.github", side_effect=api), patch("artifacts.download", return_value=data):
            return production_evidence("owner/repo", self.state, 7, "c" * 40)

    def test_success_binds_exact_profile_source_definition_and_raw_hash(self):
        proof, raw = self.verify()
        self.assertEqual(hashlib.sha256(raw).hexdigest(), proof["artifact_sha256"])
        for key in ("candidate_sha", "workflow_sha"):
            before = self.report[key]
            self.report[key] = "f" * 40
            with self.assertRaisesRegex(Rejected, "source/profile"):
                self.verify()
            self.report[key] = before

    def test_skipped_build_and_unrequested_compatibility_cannot_be_accepted(self):
        self.job["conclusion"] = "skipped"
        with self.assertRaisesRegex(Rejected, "did not pass"):
            self.verify()
        self.job["conclusion"] = "success"
        self.report["compatibility"] = True
        with self.assertRaisesRegex(Rejected, "source/profile"):
            self.verify()


if __name__ == "__main__":
    unittest.main()
