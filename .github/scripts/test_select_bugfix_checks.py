"""Check that workflow selection cannot expand or shrink dispatched evidence."""

from pathlib import Path
import unittest

import yaml

from select_bugfix_checks import select_jobs


class SelectionTests(unittest.TestCase):
    def setUp(self):
        self.contract = {"environments": ["linux-x64-net8.0", "windows-x64-net8.0"]}
        self.profile = {"profile_sha256": "a" * 64, "matrix": [
            {"os": "ubuntu-latest", "framework": "net8.0"},
            {"os": "windows-latest", "framework": "net8.0"}]}

    def test_compressed_candidate_runs_exact_selected_lanes_and_production(self):
        result = select_jobs(self.contract, self.profile, "broad", "compressed-v1", "a" * 64)
        self.assertEqual(self.profile["matrix"], result["matrix"]["include"])
        self.assertTrue(result["production"])

    def test_revalidation_can_use_one_reviewed_lane_without_original_six(self):
        self.profile["matrix"] = self.profile["matrix"][:1]
        result = select_jobs(self.contract, self.profile, "acceptance", "legacy-six-lane-v1", "a" * 64)
        self.assertEqual(1, len(result["matrix"]["include"]))
        self.assertTrue(result["production"])

    def test_baseline_only_uses_an_approved_environment_without_production(self):
        self.contract["environments"] = ["windows-x64-net8.0"]
        result = select_jobs(self.contract, None, "baseline", "compressed-v1", "")
        self.assertEqual([{"os": "windows-latest", "framework": "net8.0"}], result["matrix"]["include"])
        self.assertFalse(result["production"])

    def test_missing_or_different_candidate_digest_is_rejected(self):
        for profile, digest in ((None, "a" * 64), (self.profile, ""), (self.profile, "b" * 64)):
            with self.subTest(digest=digest), self.assertRaises(ValueError):
                select_jobs(self.contract, profile, "broad", "compressed-v1", digest)

    def test_baseline_profile_and_unknown_modes_are_rejected(self):
        with self.assertRaises(ValueError):
            select_jobs(self.contract, self.profile, "baseline", "compressed-v1", "a" * 64)
        with self.assertRaises(ValueError):
            select_jobs(self.contract, None, "baseline", "skip-checks", "")
        with self.assertRaises(ValueError):
            select_jobs(self.contract, self.profile, "skip-checks", "compressed-v1", "a" * 64)

    def test_workflow_binds_inputs_and_keeps_production_parallel_and_isolated(self):
        path = Path(__file__).resolve().parents[1] / "workflows/bugfix-check.yml"
        workflow = yaml.load(path.read_text(encoding="utf-8"), Loader=yaml.BaseLoader)
        jobs = workflow["jobs"]
        self.assertEqual("github.event_name == 'workflow_dispatch'", jobs["select"]["if"])
        self.assertEqual("select", jobs["checks"]["needs"])
        self.assertEqual("select", jobs["production"]["needs"])
        self.assertNotIn("compatibility", jobs)
        check = next(step for step in jobs["checks"]["steps"]
                     if step.get("run") == "python control/.github/scripts/grade_bugfix_checks.py")
        self.assertEqual("${{ inputs.acceptance_profile_sha256 }}", check["env"]["ACCEPTANCE_PROFILE_SHA256"])
        self.assertEqual("${{ inputs.protocol }}", check["env"]["PROTOCOL"])
        production = jobs["production"]["steps"]
        self.assertTrue(any(step.get("with", {}).get("path") == "candidate" for step in production))
        self.assertTrue(any(step.get("with", {}).get("name") == "bugfix-production" for step in production))


if __name__ == "__main__":
    unittest.main()
