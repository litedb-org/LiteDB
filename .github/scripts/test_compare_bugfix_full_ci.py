from contextlib import redirect_stdout
import copy
import hashlib
import io
import json
from pathlib import Path
import tempfile
import unittest

from compare_bugfix_full_ci import main


BASE_SHA = "a" * 40
CANDIDATE_SHA = "b" * 40
DEFINITION_SHA = "c" * 40
REGRESSION = "Tests.Issue2874.Invalid(value: 1)"
CONTROL = "Tests.Issue2874.Valid"
KNOWN_FAILURE = "Tests.Issue999.Known"
PASSING = "Tests.Ordinary.Passes"
INTERMITTENT = "Tests.Intermittent.Case"
INTERMITTENT_CLASS = "Tests.Intermittent"
QUARANTINE = {
    "schema_version": 1, "expected_original_jobs": 6, "expected_remaining_jobs": 3,
    "quarantines": [{"kind": "repro", "issue": 2854,
                     "repro": "Issue_2854_CircularPageList", "status": "unverified",
                     "authorized_on": "2026-09-15", "authorization": "test",
                     "reason": "missing helper", "jobs": [
                         "repro-runner / Run Issue_2854_CircularPageList on one",
                         "repro-runner / Run Issue_2854_CircularPageList on two",
                         "repro-runner / Run Issue_2854_CircularPageList on three"]}]}
QUARANTINE_RAW = json.dumps(QUARANTINE).encode()
QUARANTINE_SHA = hashlib.sha256(QUARANTINE_RAW).hexdigest()
FAILURE_POLICY_RAW = b'{"schema_version":1,"tests":{}}'
FAILURE_POLICY_SHA = hashlib.sha256(FAILURE_POLICY_RAW).hexdigest()


def test(name, outcome="passed", classification=None, failure=None):
    value = {"identity": name + "#0", "name": name,
             "class_name": name.rsplit(".", 1)[0], "outcome": outcome}
    if outcome == "failed":
        value["failure_classification"] = classification or "assertion: known"
        value["failure"] = failure or "Known failure"
    return value


def bundle(run_id, sha, target_passes=False, repro_verdict="bug_present", role=None):
    regression = test(REGRESSION, "passed" if target_passes else "failed",
                      "assertion: IndexOutOfRangeException",
                      "Expected ArgumentException\nSystem.IndexOutOfRangeException")
    jobs = [
        {"name": "full tests", "kind": "tests", "status": "completed",
         "conclusion": "failure", "tests": [regression, test(CONTROL),
                                               test(KNOWN_FAILURE, "failed"), test(PASSING)],
         "runtime_architecture": "x64", "claimed_architecture": "x64",
         "architecture_verified": True},
        {"name": "Issue_2854 repro", "kind": "repro", "status": "completed",
         "conclusion": "success" if repro_verdict in ("bug_present", "behavior_correct") else "failure",
         "verdict": repro_verdict, "classification": "confirmed-cycle"},
        {"name": "build", "kind": "check", "status": "completed", "conclusion": "success"},
    ]
    return {
        "schema_version": 1,
        "accepted": True,
        "issue": 2874,
        "role": role or ("baseline" if run_id == 10 else "candidate"),
        "source_sha": sha,
        "evidence_definition_sha": DEFINITION_SHA,
        "failure_normalization_sha256": FAILURE_POLICY_SHA,
        "quarantine_sha256": QUARANTINE_SHA,
        "coverage_gaps": QUARANTINE["quarantines"],
        "run": {"id": run_id, "head_sha": DEFINITION_SHA,
                "workflow_path": ".github/workflows/bugfix-full-ci.yml",
                "event": "workflow_dispatch", "status": "completed", "conclusion": "failure"},
        "jobs": jobs,
    }


class FullCiComparisonTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.root = Path(self.directory.name)
        self.manifest = self.root / "issues.json"
        self.baseline_path = self.root / "baseline.json"
        self.candidate_path = self.root / "candidate.json"
        self.output = self.root / "result.json"
        self.quarantine = self.root / "quarantine.json"
        self.quarantine.write_bytes(QUARANTINE_RAW)
        self.failure_policy = self.root / "failure-normalization.json"
        self.failure_policy.write_bytes(FAILURE_POLICY_RAW)
        self.baseline_policy = self.root / "known-failure-classes.json"
        self.baseline_policy.write_text(json.dumps({
            "schema_version": 1,
            "classes": ["Tests.Issue2874", "Tests.Issue999", INTERMITTENT_CLASS],
            "skipped_tests": [],
            "intermittent_classes": [INTERMITTENT_CLASS],
        }), encoding="utf-8")
        self.manifest.write_text(json.dumps({
            "schema_version": 1,
            "issues": {"2874": {
                "regressions": [{"name": REGRESSION,
                                 "failure_first_line": "Expected ArgumentException",
                                 "failure_contains": ["System.IndexOutOfRangeException"]}],
                "controls": [{"name": CONTROL}],
            }},
        }), encoding="utf-8")
        self.baseline = bundle(10, BASE_SHA)
        self.candidate = bundle(11, CANDIDATE_SHA, target_passes=True)

    def tearDown(self):
        self.directory.cleanup()

    def run_gate(self):
        self.baseline_path.write_text(json.dumps(self.baseline), encoding="utf-8")
        self.candidate_path.write_text(json.dumps(self.candidate), encoding="utf-8")
        arguments = ["--baseline", str(self.baseline_path), "--candidate", str(self.candidate_path),
                     "--manifest", str(self.manifest), "--issue", "2874",
                     "--base-sha", BASE_SHA, "--candidate-sha", CANDIDATE_SHA,
                     "--quarantine", str(self.quarantine), "--expected-target-job-count", "1",
                     "--failure-normalization", str(self.failure_policy),
                     "--baseline-policy", str(self.baseline_policy),
                     "--output", str(self.output)]
        with redirect_stdout(io.StringIO()):
            exit_code = main(arguments)
        return exit_code, json.loads(self.output.read_text(encoding="utf-8"))

    def assert_rejected(self, text):
        exit_code, report = self.run_gate()
        self.assertEqual(1, exit_code)
        self.assertFalse(report["accepted"])
        self.assertIn(text, json.dumps(report))

    def test_accepts_target_fix_and_identical_known_failures(self):
        exit_code, report = self.run_gate()
        self.assertEqual(0, exit_code)
        self.assertTrue(report["accepted"])
        self.assertEqual("behavior_correct", report["outcome"])
        self.assertEqual(["full tests"], report["target_jobs"])
        self.assertEqual(["full tests :: " + KNOWN_FAILURE], report["known_failures"])
        self.assertEqual(2854, report["coverage_gaps"][0]["issue"])

    def test_rejects_untrusted_or_executed_quarantine(self):
        self.candidate["quarantine_sha256"] = "d" * 64
        self.assert_rejected("different quarantine policy")
        self.candidate = bundle(11, CANDIDATE_SHA, target_passes=True)
        excluded = QUARANTINE["quarantines"][0]["jobs"][0]
        self.candidate["jobs"][2] = {
            "name": excluded, "kind": "repro", "status": "completed",
            "conclusion": "success", "verdict": "bug_present", "classification": "unexpected"}
        self.baseline["jobs"][2] = copy.deepcopy(self.candidate["jobs"][2])
        self.assert_rejected("executed an explicitly quarantined job")

    def test_rejects_failure_replacement_even_when_failure_count_is_unchanged(self):
        tests = self.candidate["jobs"][0]["tests"]
        next(item for item in tests if item["name"] == KNOWN_FAILURE)["outcome"] = "passed"
        known = next(item for item in tests if item["name"] == KNOWN_FAILURE)
        known.pop("failure", None)
        known.pop("failure_classification", None)
        next(item for item in tests if item["name"] == PASSING)["outcome"] = "failed"
        passing = next(item for item in tests if item["name"] == PASSING)
        passing.update(failure="New crash", failure_classification="exception: crash")
        self.assert_rejected("unexpected_passes")
        self.assert_rejected("Outcome changed passed -> failed")

    def test_rejects_missing_newly_skipped_and_reclassified_tests(self):
        for mutation, expected in (
            (lambda tests: tests.pop(), "missing tests"),
            (lambda tests: tests.append(test("Tests.New.Case", "skipped")), "New candidate test"),
            (lambda tests: next(item for item in tests if item["name"] == KNOWN_FAILURE)
             .update(failure_classification="exception: different"), "classification changed"),
        ):
            with self.subTest(expected=expected):
                original = copy.deepcopy(self.candidate)
                mutation(self.candidate["jobs"][0]["tests"])
                self.assert_rejected(expected)
                self.candidate = original

    def test_rejects_changed_or_partial_job_matrix(self):
        self.candidate["jobs"][0]["name"] = "renamed full tests"
        self.assert_rejected("Job matrix changed")
        self.candidate = bundle(11, CANDIDATE_SHA, target_passes=True)
        self.baseline["jobs"][0]["tests"] = self.baseline["jobs"][0]["tests"][1:]
        self.assert_rejected("Expected target coverage")

    def test_rejects_unconfirmed_repro_harness_failure(self):
        self.baseline = bundle(10, BASE_SHA, repro_verdict="harness_error")
        self.candidate = bundle(11, CANDIDATE_SHA, True, repro_verdict="harness_error")
        self.assert_rejected("baseline repro is harness_error")

    def test_rejects_wrong_baseline_defect_and_target_that_still_fails(self):
        regression = self.baseline["jobs"][0]["tests"][0]
        regression["failure"] = "Some other assertion"
        self.assert_rejected("wrong defect")
        self.baseline = bundle(10, BASE_SHA)
        self.candidate = bundle(11, CANDIDATE_SHA)
        self.assert_rejected("Target did not pass")

    def test_rejects_stale_or_incomplete_run_provenance(self):
        self.candidate["source_sha"] = "d" * 40
        self.assert_rejected("wrong commit")
        self.candidate = bundle(11, CANDIDATE_SHA, target_passes=True)
        self.candidate["jobs"][0]["status"] = "in_progress"
        self.assert_rejected("did not complete")

    def test_rejects_wrong_role_or_failure_normalization_policy(self):
        self.candidate["role"] = "baseline"
        self.assert_rejected("wrong collection role")
        self.candidate = bundle(11, CANDIDATE_SHA, target_passes=True)
        self.candidate["failure_normalization_sha256"] = "d" * 64
        self.assert_rejected("different failure-normalization policy")

    def test_reports_reviewed_intermittent_flip_as_structured_inconclusive(self):
        self.baseline["jobs"][0]["tests"].append(test(INTERMITTENT))
        self.candidate["jobs"][0]["tests"].append(
            test(INTERMITTENT, "failed", "assertion: intermittent"))
        self.candidate["jobs"][0]["conclusion"] = "failure"
        exit_code, report = self.run_gate()
        self.assertEqual(1, exit_code)
        self.assertEqual([], report["errors"])
        self.assertEqual([], report["unexpected_passes"])
        self.assertEqual([{
            "job": "full tests", "name": INTERMITTENT,
            "class_name": INTERMITTENT_CLASS,
            "baseline_outcome": "passed", "candidate_outcome": "failed",
        }], report["inconclusive_changes"])

    def test_rejects_unclassified_baseline_failures_and_skips(self):
        self.baseline["jobs"][0]["tests"].append(
            test("Tests.Unknown.Failure", "failed", "assertion: unknown"))
        self.candidate["jobs"][0]["tests"].append(
            test("Tests.Unknown.Failure", "failed", "assertion: unknown"))
        self.assert_rejected("Unclassified baseline failure")
        self.baseline = bundle(10, BASE_SHA)
        self.candidate = bundle(11, CANDIDATE_SHA, target_passes=True)
        self.baseline["jobs"][0]["tests"].append(test("Tests.Unknown.Skip", "skipped"))
        self.candidate["jobs"][0]["tests"].append(test("Tests.Unknown.Skip", "skipped"))
        self.assert_rejected("Unclassified baseline skip")


if __name__ == "__main__":
    unittest.main()
