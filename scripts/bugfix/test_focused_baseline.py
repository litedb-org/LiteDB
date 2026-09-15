"""Environment-aware focused baseline contracts are exact and fail closed."""

import copy
import hashlib
import json
from pathlib import Path
import sys
import unittest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts/bugfix"))

from failure_normalization import load_failure_normalization
from focused_baseline import FocusedBaselineError, validate
from policy import load_issue, verify_focused, verify_focused_pair
from trx import GateError, TestResult, TestRun


MANIFEST = ROOT / "scripts/bugfix/issues.json"
FIXTURE = ROOT / "scripts/bugfix/fixtures/wave-three-baseline.json"
FAILURE_FIXTURE = ROOT / "scripts/bugfix/fixtures/wave-three-focused-failures.json"
NORMALIZATION = ROOT / "scripts/bugfix/failure-normalization.json"


class FocusedBaselineContractTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.policy, cls.policy_sha = load_failure_normalization(NORMALIZATION)
        cls.fixture = json.loads(FIXTURE.read_text(encoding="utf-8"))
        cls.failure_fixture = json.loads(FAILURE_FIXTURE.read_text(encoding="utf-8"))

    def issue(self, number):
        return load_issue(MANIFEST, number)[0]

    def run_2860(self, environment, baseline):
        issue = self.issue(2860)
        tests = {}
        definitions = {}
        for case in issue["regressions"] + issue["controls"]:
            expected = case["baseline_by_environment"][environment]
            failed = baseline and expected["outcome"] == "Failed"
            message = ""
            if failed:
                classification = case["failure_classifications"][
                    expected["classification"]]
                message = classification["failure_first_line"]
                self.assertEqual(classification["sha256"], hashlib.sha256(
                    message.encode("utf-8")).hexdigest())
            result = TestResult(case["name"], "Failed" if failed else "Passed",
                                message, "LiteDB.Tests.Issues.Issue2860_Tests",
                                case["test_id"])
            tests[case["name"]] = result
            definitions[case["test_id"]] = case["name"]
        return TestRun(tests, "a" * 64, definitions)

    def verify(self, run, baseline, environment):
        verify_focused(run, self.issue(2860), baseline, environment,
                       self.policy, self.policy_sha)

    def test_six_observed_artifacts_and_exact_cases_are_retained(self):
        manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))
        self.assertEqual(34988724926, self.fixture["run_id"])
        self.assertEqual("dd937719f7eee53c512f50ac604cab639bf42a4c",
                         self.fixture["source_revision"])
        self.assertEqual(self.policy_sha,
                         self.fixture["failure_normalization_sha256"])
        artifacts = {item["environment"]: item for item in self.fixture["artifacts"]}
        for number in (2860, 2870):
            contract = manifest["issues"][str(number)]
            self.assertEqual(contract["focused_baseline"]["artifacts"].keys(),
                             artifacts.keys())
            for environment, expected in contract["focused_baseline"]["artifacts"].items():
                actual = artifacts[environment]
                self.assertEqual(expected["artifact_id"], actual["id"])
                self.assertEqual(expected["artifact_name"], actual["name"])
                self.assertEqual(expected["baseline_trx_sha256"],
                                 actual["baseline_trx_sha256"])
                self.assertEqual(expected["repeat_candidate_trx_sha256"],
                                 actual["repeat_candidate_trx_sha256"])
            observed = self.fixture["deferred"][str(number)][
                "resolved_focused_baseline"]
            self.assertEqual(contract["regressions"], observed["regressions"])
            self.assertEqual(contract["controls"], observed["controls"])

    def test_supporting_canonical_failures_are_hash_checked_and_reviewable(self):
        support = self.failure_fixture
        self.assertEqual(self.fixture["run_id"], support["source_run_id"])
        self.assertEqual(self.fixture["source_revision"], support["source_sha"])
        self.assertEqual(self.fixture["workflow_sha"], support["workflow_sha"])
        self.assertEqual(self.policy_sha, support["failure_normalization_sha256"])
        self.assertEqual(self.fixture["artifacts"], support["artifacts"])
        for number in (2860, 2870):
            issue = self.issue(number)
            retained = support["issues"][str(number)]["regressions"]
            self.assertEqual({case["name"] for case in issue["regressions"]},
                             set(retained))
            for case in issue["regressions"]:
                evidence = retained[case["name"]]
                self.assertEqual(case["test_id"], evidence["test_id"])
                self.assertEqual(set(case["failure_classifications"]),
                                 set(evidence["classifications"]))
                for label, failure in evidence["classifications"].items():
                    classification = case["failure_classifications"][label]
                    self.assertEqual(classification["sha256"], hashlib.sha256(
                        failure.encode("utf-8")).hexdigest())
                    self.assertEqual(classification["failure_first_line"],
                                     failure.splitlines()[0])

    def test_2860_uses_exact_os_specific_defect_and_preserves_controls(self):
        issue = self.issue(2860)
        self.assertEqual((8, 6), (len(issue["regressions"]), len(issue["controls"])))
        self.assertTrue({case["name"] for case in issue["regressions"]}.isdisjoint(
            self.policy))
        rooted = [case for case in issue["regressions"]
                  if 'text: "/a/b"' in case["name"]]
        self.assertEqual(2, len(rooted))
        for case in rooted:
            self.assertEqual("unix", case["baseline_by_environment"][
                "linux-x64-net8.0"]["classification"])
            self.assertEqual("windows", case["baseline_by_environment"][
                "windows-x64-net8.0"]["classification"])
        for environment in ("linux-x64-net8.0", "windows-x64-net10.0"):
            baseline = self.run_2860(environment, True)
            candidate = self.run_2860(environment, False)
            self.verify(baseline, True, environment)
            self.verify(candidate, False, environment)
            verify_focused_pair(baseline, candidate, issue)

    def test_wrong_environment_failure_identity_or_policy_is_rejected(self):
        environment = "windows-x64-net8.0"
        run = self.run_2860(environment, True)
        rooted = next(test for test in run.tests.values()
                      if 'text: "/a/b"' in test.name)
        run.tests[rooted.name] = TestResult(
            rooted.name, "Failed",
            "Expected actual.IsAbsoluteUri to be False, but found True.",
            rooted.class_name, rooted.test_id)
        with self.assertRaisesRegex(GateError, "expected defect"):
            self.verify(run, True, environment)

        run = self.run_2860(environment, True)
        name = next(iter(run.tests))
        original = run.tests[name]
        run.tests[name] = TestResult(original.name, original.outcome,
                                     original.message, original.class_name,
                                     "00000000-0000-0000-0000-000000000000")
        with self.assertRaisesRegex(GateError, "identity changed"):
            self.verify(run, True, environment)
        with self.assertRaisesRegex(GateError, "policy or environment"):
            verify_focused(self.run_2860(environment, True), self.issue(2860),
                           True, environment, self.policy, "0" * 64)

    def test_components_have_controls_or_an_explicit_gap(self):
        issue = self.issue(2860)
        gaps = issue["focused_baseline"]["control_gaps"]
        self.assertEqual(["max-depth-diagnostic"],
                         [gap["component"] for gap in gaps])
        self.assertIn("URI controls do not count", gaps[0]["reason"])
        changed = copy.deepcopy(issue)
        changed["focused_baseline"]["control_gaps"] = []
        with self.assertRaisesRegex(FocusedBaselineError, "control"):
            validate(changed)

    def test_2870_reuses_only_the_reviewed_exact_case_normalization(self):
        issue = self.issue(2870)
        self.assertEqual((4, 3), (len(issue["regressions"]), len(issue["controls"])))
        self.assertEqual(self.policy_sha,
                         issue["focused_baseline"]["failure_normalization_sha256"])
        normalized_names = set(self.policy)
        for case in issue["regressions"]:
            self.assertIn(case["name"], normalized_names)
            unix = case["baseline_by_environment"]["linux-x64-net8.0"]
            windows = case["baseline_by_environment"]["windows-x64-net8.0"]
            self.assertEqual("unix", unix["classification"])
            self.assertEqual("windows", windows["classification"])
            self.assertNotEqual(case["failure_classifications"]["unix"]["sha256"],
                                case["failure_classifications"]["windows"]["sha256"])
        self.assertEqual([], issue["focused_baseline"]["control_gaps"])


if __name__ == "__main__":
    unittest.main()
