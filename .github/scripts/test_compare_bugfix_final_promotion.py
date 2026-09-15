"""Final promotion evaluates the complete accepted ledger as one target set."""

import copy
import hashlib
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

import compare_bugfix_final_promotion as promotion
from source_context import CLASS as GUARD_CLASS, DECLARATION, TEST as GUARD_TEST


BASE = "a" * 40
FINAL = "b" * 40
TEST_SOURCE = BASE
REGRESSION = "LiteDB.Tests.Issues.Issue100_Tests.Regression"
CONTROL = "LiteDB.Tests.Issues.Issue100_Tests.Control"
KNOWN = "LiteDB.Tests.Issues.Issue200_Tests.Known"
CLASS = "LiteDB.Tests.Issues.Issue100_Tests"
KNOWN_CLASS = "LiteDB.Tests.Issues.Issue200_Tests"
ROOT = Path(__file__).resolve().parents[2]


def result(identity, name, outcome, failure=""):
    value = {"identity": identity, "name": name, "class_name": name.rsplit(".", 1)[0],
             "outcome": outcome}
    if outcome == "failed":
        value.update(failure=failure, failure_classification=failure)
    return value


def test_job(tests, architecture_verified=None, runtime="x86_64"):
    value = {"name": "build-and-test / Test (Linux .NET 8)", "kind": "tests",
             "status": "completed", "conclusion": "failure", "tests": tests,
             "runtime_architecture": runtime}
    if architecture_verified is not None:
        value.update(architecture_verified=architecture_verified,
                     claimed_architecture="arm64")
    return value


def accepted(required_environments=None, repros=None):
    return {100: {
        "entry": {},
        "regressions": [{"name": REGRESSION, "failure_first_line": "expected red",
                         "failure_contains": ["detail"]}],
        "controls": [{"name": CONTROL}],
        "contract": {"required_environments": required_environments or []},
        "repro_roles": repros or {},
    }}


def evidence(jobs, run_id):
    return ({"evidence_definition_sha": "c" * 40, "run": {"id": run_id},
             "coverage_gaps": [{"issue": 2854}]}, {job["name"]: job for job in jobs})


def focused_accepted(issue_number):
    contract = json.loads((ROOT / "scripts/bugfix/issues.json").read_text(
        encoding="utf-8"))["issues"][str(issue_number)]
    return {issue_number: {
        "entry": {}, "regressions": contract["regressions"],
        "controls": contract["controls"], "contract": contract,
        "focused_baseline_sha256": promotion.focused_baseline_digest(contract),
        "repro_roles": {},
    }}


def focused_baseline_job(issue_number, environment, name):
    accepted_contract = focused_accepted(issue_number)
    contract = accepted_contract[issue_number]["contract"]
    support = json.loads((ROOT /
        "scripts/bugfix/fixtures/wave-three-focused-failures.json").read_text(
            encoding="utf-8"))["issues"][str(issue_number)]["regressions"]
    tests = []
    for case in contract["regressions"]:
        expected = case["baseline_by_environment"][environment]
        failure = support[case["name"]]["classifications"][
            expected["classification"]]
        item = result(case["test_id"], case["name"], "failed", failure)
        item["test_id"] = case["test_id"]
        tests.append(item)
    for case in contract["controls"]:
        item = result(case["test_id"], case["name"], "passed")
        item["test_id"] = case["test_id"]
        tests.append(item)
    return accepted_contract, {
        "name": name, "kind": "tests", "status": "completed",
        "conclusion": "failure", "tests": tests,
        "runtime_architecture": environment.split("-")[1],
        "claimed_architecture": environment.split("-")[1],
        "architecture_verified": True, "claimed_architecture_matches": True,
    }


class MultiIssueComparisonTests(unittest.TestCase):
    def setUp(self):
        self.before = test_job([
            result("r", REGRESSION, "failed", "expected red\ndetail"),
            result("c", CONTROL, "passed"),
            result("k", KNOWN, "failed", "known failure"),
        ])
        self.after = copy.deepcopy(self.before)
        self.after["tests"][0] = result("r", REGRESSION, "passed")

    def compare(self, before_jobs=None, after_jobs=None, contracts=None):
        before_jobs = before_jobs or [self.before]
        after_jobs = after_jobs or [self.after]
        with patch.object(promotion, "EXPECTED_ORDINARY_TEST_JOBS", 1):
            with patch.object(promotion, "EXPECTED_REPRO_PLATFORMS", 1):
                return promotion.compare(evidence(before_jobs, 1), evidence(after_jobs, 2),
                                         contracts or accepted(), {CLASS, KNOWN_CLASS}, set(), set())

    def test_all_accepted_tests_are_one_allowed_red_to_green_target_set(self):
        report = self.compare()
        self.assertTrue(report["accepted"])
        self.assertEqual([100], report["accepted_issues"])
        self.assertEqual([self.before["name"]], report["accepted_test_jobs"])
        self.assertEqual([f"{self.before['name']} :: {KNOWN}"],
                         report["remaining_known_failures"])

    def test_nonaccepted_unexpected_pass_remains_blocking(self):
        self.after["tests"][2] = result("k", KNOWN, "passed")
        report = self.compare()
        self.assertFalse(report["accepted"])
        self.assertEqual([f"{self.before['name']} :: {KNOWN}"],
                         report["unexpected_passes"])

    def test_declared_guard_missing_from_both_ordinary_runs_blocks_final_promotion(self):
        with patch.object(promotion, "EXPECTED_ORDINARY_TEST_JOBS", 1):
            report = promotion.compare(evidence([self.before], 1), evidence([self.after], 2), accepted(),
                                       {CLASS, KNOWN_CLASS}, set(), set(), [{"test_name": GUARD_TEST}])
        self.assertFalse(report["accepted"])
        self.assertTrue(any("Source guard observation is missing from required ordinary job" in error
                            for error in report["errors"]))

    def test_every_ordinary_lane_must_contain_every_accepted_case(self):
        partial = copy.deepcopy(self.after)
        partial["name"] = "build-and-test / Test (macOS .NET 8)"
        partial["tests"] = partial["tests"][1:]
        report = self.compare(after_jobs=[self.after, partial])
        self.assertFalse(report["accepted"])
        self.assertTrue(any("job matrices differ" in error for error in report["errors"]))

    def test_only_an_explicit_required_architecture_becomes_a_blocker(self):
        self.before.update(architecture_verified=False, claimed_architecture="arm64")
        self.after.update(architecture_verified=False, claimed_architecture="arm64")
        report = self.compare()
        self.assertTrue(report["accepted"])
        self.assertTrue(report["architecture_limitations"])
        report = self.compare(contracts=accepted(["linux-arm64-net8.0"]))
        self.assertFalse(report["accepted"])
        self.assertIn("Accepted issue 100 requires unproven linux-arm64-net8.0 execution",
                      report["blockers"])
        self.before.update(architecture_verified=True, runtime_architecture="arm64")
        self.after.update(architecture_verified=True, runtime_architecture="arm64")
        self.assertTrue(self.compare(contracts=accepted(
            ["linux-arm64-net8.0"]))["accepted"])
        report = self.compare(contracts=accepted(["windows-x64-net8.0"]))
        self.assertFalse(report["accepted"])
        self.assertIn("Accepted issue 100 requires unproven windows-x64-net8.0 execution",
                      report["blockers"])
        for job in (self.before, self.after):
            job.update(name="build-and-test / Test (Windows windows-2022 - x64 - .NET 8)",
                       architecture_verified=True, runtime_architecture="x64",
                       claimed_architecture="x64", claimed_architecture_matches=True)
        self.assertTrue(self.compare(contracts=accepted(
            ["windows-x64-net8.0"]))["accepted"])
        for job in (self.before, self.after):
            job.update(claimed_architecture="x86", claimed_architecture_matches=False)
        mismatched_label = self.compare(contracts=accepted(["windows-x64-net8.0"]))
        self.assertTrue(mismatched_label["accepted"])
        self.assertTrue(mismatched_label["architecture_limitations"])

    def test_environment_aware_baselines_use_exact_measured_lane_classifications(self):
        linux, linux_job = focused_baseline_job(
            2860, "linux-x64-net8.0", "build-and-test / Test (Linux .NET 8)")
        errors = []
        promotion.baseline_target_contract(linux_job, linux, errors)
        self.assertEqual([], errors)

        windows, windows_job = focused_baseline_job(
            2860, "windows-x64-net8.0",
            "build-and-test / Test (Windows windows-2022 - x64 - .NET 8)")
        errors = []
        promotion.baseline_target_contract(windows_job, windows, errors)
        self.assertEqual([], errors)
        rooted = next(test for test in windows_job["tests"] if 'text: "/a/b"' in test["name"])
        rooted["failure_classification"] = next(
            test["failure_classification"] for test in linux_job["tests"]
            if test["name"] == rooted["name"])
        errors = []
        promotion.baseline_target_contract(windows_job, windows, errors)
        self.assertTrue(any("wrong canonical baseline defect" in error for error in errors))
        _, identity_job = focused_baseline_job(
            2860, "linux-x64-net8.0", "build-and-test / Test (Linux .NET 8)")
        identity_job["tests"][0]["test_id"] = "00000000-0000-0000-0000-000000000000"
        errors = []
        promotion.baseline_target_contract(identity_job, linux, errors)
        self.assertTrue(any("test identity changed" in error for error in errors))

    def test_2870_reuses_the_retained_reviewed_canonical_classifications(self):
        accepted_contract, job = focused_baseline_job(
            2870, "linux-x64-net10.0", "build-and-test / Test (Linux .NET 10)")
        errors = []
        promotion.baseline_target_contract(job, accepted_contract, errors)
        self.assertEqual([], errors)

    def test_accepted_repro_may_only_move_bug_present_to_behavior_correct(self):
        old_repro = {"name": "repro-runner / Run Issue_100_Case on ubuntu-22.04",
                     "kind": "repro", "status": "completed", "conclusion": "success",
                     "verdict": "bug_present", "classification": "old"}
        new_repro = {**old_repro, "verdict": "behavior_correct",
                     "classification": "new"}
        contracts = accepted(repros={"Issue_100_Case": "regression"})
        report = self.compare([self.before, old_repro], [self.after, new_repro],
                              contracts=contracts)
        self.assertTrue(report["accepted"])
        self.assertEqual([old_repro["name"]], report["accepted_repro_transitions"])
        unchanged_green = {**old_repro, "verdict": "behavior_correct",
                           "classification": "already green"}
        report = self.compare([self.before, unchanged_green],
                              [self.after, copy.deepcopy(unchanged_green)],
                              contracts=contracts)
        self.assertFalse(report["accepted"])
        self.assertTrue(any("lacks bug_present -> behavior_correct" in error
                            for error in report["errors"]))
        baseline_no_repro = {**old_repro, "verdict": "behavior_correct",
                             "classification": "no repro"}
        report = self.compare([self.before, baseline_no_repro],
                              [self.after, new_repro], contracts=contracts)
        self.assertFalse(report["accepted"])
        report = self.compare([self.before, old_repro], [self.after, new_repro])
        self.assertFalse(report["accepted"])
        self.assertTrue(any("no explicit regression/control role" in error
                            for error in report["errors"]))
        green_control = {**old_repro, "verdict": "behavior_correct",
                         "classification": "stable control"}
        report = self.compare(
            [self.before, green_control], [self.after, copy.deepcopy(green_control)],
            contracts=accepted(repros={"Issue_100_Case": "control"}))
        self.assertTrue(report["accepted"])
        report = self.compare(contracts=accepted(
            repros={"Issue_100_Missing": "regression"}))
        self.assertFalse(report["accepted"])
        self.assertTrue(any("ran in 0" in error for error in report["errors"]))
        nonaccepted = copy.deepcopy(old_repro)
        nonaccepted["name"] = "repro-runner / Run Issue_101_Case on ubuntu-22.04"
        changed = {**nonaccepted, "verdict": "behavior_correct", "classification": "new"}
        report = self.compare([self.before, nonaccepted], [self.after, changed])
        self.assertFalse(report["accepted"])
        self.assertTrue(any("Nonaccepted repro" in error for error in report["errors"]))


class SourceObservationComparisonTests(unittest.TestCase):
    def setUp(self):
        self.observation = {"issue": 2871, **DECLARATION, "behavior_unverified": True,
                            "issue_credit": False, "baseline_failure": "Exact frozen source guard failure"}
        previous = result("guard", GUARD_TEST, "failed", self.observation["baseline_failure"])
        previous["class_name"] = GUARD_CLASS
        self.before = test_job([previous])
        current = {"identity": "guard", "name": GUARD_TEST, "class_name": GUARD_CLASS, "outcome": "passed"}
        self.after = test_job([current])

    def compare(self):
        errors, unexpected, inconclusive, observations = [], [], [], []
        promotion.compare_test_job(self.before, self.after, set(), set(), errors, unexpected,
                                   inconclusive, [self.observation], observations)
        return errors, unexpected, observations

    def test_only_declared_source_guard_is_observed_without_issue_credit(self):
        errors, unexpected, observations = self.compare()
        self.assertEqual(([], []), (errors, unexpected))
        self.assertEqual([{**self.observation, "job": self.before["name"]}], observations)
        self.assertTrue(observations[0]["behavior_unverified"])
        self.assertFalse(observations[0]["issue_credit"])

    def test_behavioral_m111_and_other_guard_passes_remain_unexpected(self):
        for name in ("LiteDB.Tests.Audit2026.ExpressionAuditRegression_Tests.M111_uint64_round_trips_through_bson_value",
                     GUARD_TEST.replace("id: 132,", "id: 111,")):
            self.before["tests"].append(result(name, name, "failed", "Original failure"))
            self.after["tests"].append(result(name, name, "passed"))
        errors, unexpected, observations = self.compare()
        self.assertEqual([], errors)
        self.assertEqual(2, len(unexpected))
        self.assertEqual(1, len(observations))

    def test_missing_skipped_or_wrong_failure_guard_cannot_be_observed(self):
        self.after["tests"][0]["outcome"] = "skipped"
        self.assertTrue(self.compare()[0])
        self.after["tests"] = []
        self.assertTrue(self.compare()[0])
        self.after = copy.deepcopy(self.before)
        self.after["tests"][0]["outcome"] = "passed"
        self.before["tests"][0]["failure"] = "Wrong failure"
        with self.assertRaisesRegex(ValueError, "exact baseline"):
            self.compare()


class AcceptedLedgerTests(unittest.TestCase):
    def git(self, root, *args):
        return subprocess.run(["git", "-C", str(root), *args], check=True,
                              capture_output=True, text=True).stdout.strip()

    def test_immutable_git_objects_bind_ledger_contract_and_frozen_test_blob(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.git(root, "init", "--quiet")
            self.git(root, "config", "user.email", "test@example.com")
            self.git(root, "config", "user.name", "Test")
            test_path = root / "LiteDB.Tests/Issues/Issue100_Tests.cs"
            test_path.parent.mkdir(parents=True)
            test_path.write_text("// frozen\n", encoding="utf-8")
            repro_path = root / "LiteDB.ReproRunner/Repros.txt"
            repro_path.parent.mkdir(parents=True)
            repro_path.write_text("frozen repros\n", encoding="utf-8")
            (root / "tests.runsettings").write_text("<RunSettings />\n",
                                                     encoding="utf-8")
            self.git(root, "add", ".")
            self.git(root, "commit", "--quiet", "-m", "base")
            base = self.git(root, "rev-parse", "HEAD")
            blob = self.git(root, "rev-parse", f"{base}:LiteDB.Tests/Issues/Issue100_Tests.cs")
            production = root / "LiteDB/Value.cs"
            production.parent.mkdir()
            production.write_text("// fixed\n", encoding="utf-8")
            self.git(root, "add", ".")
            self.git(root, "commit", "--quiet", "-m", "fix")
            final = self.git(root, "rev-parse", "HEAD")
            ledger = {"schema_version": 1, "issues": {"100": {
                "base_sha": base, "candidate_sha": final,
                "candidate_tree_sha": self.git(
                    root, "rev-parse", final + "^{tree}"),
                "test_source_sha": base, "tests": [CONTROL, REGRESSION]}}}
            manifest = {"schema_version": 1, "issues": {"100": {
                "inventory_issue": 100, "frozen_test_revision": base,
                "frozen_test_blobs": {"LiteDB.Tests/Issues/Issue100_Tests.cs": blob},
                "regressions": [{"name": REGRESSION}],
                "controls": [{"name": CONTROL}]}}}
            ledger_path, manifest_path = root / "ledger.json", root / "manifest.json"
            ledger_path.write_text(json.dumps(ledger), encoding="utf-8")
            manifest_path.write_text(json.dumps(manifest), encoding="utf-8")
            state_ledger = root / "accepted-tests.json"
            state_ledger.write_bytes(ledger_path.read_bytes())
            self.git(root, "add", "accepted-tests.json")
            self.git(root, "commit", "--quiet", "-m", "state")
            state = self.git(root, "rev-parse", "HEAD")
            promotion.verify_ledger_source(root, state, ledger_path)
            ledger_path.write_text(json.dumps({"schema_version": 1, "issues": {}}),
                                   encoding="utf-8")
            with self.assertRaisesRegex(promotion.PromotionError,
                                        "exact state commit"):
                promotion.verify_ledger_source(root, state, ledger_path)
            ledger_path.write_bytes(state_ledger.read_bytes())
            loaded, _, _, blobs, immutable = promotion.load_accepted_contracts(
                root, ledger_path, manifest_path, base, final, base,
                promotion.canonical_digest(ledger))
            self.assertEqual({100}, set(loaded))
            self.assertEqual(blob, blobs["LiteDB.Tests/Issues/Issue100_Tests.cs"])
            self.assertEqual({"LiteDB.Tests", "LiteDB.ReproRunner", "tests.runsettings"},
                             set(immutable))
            test_path.write_text("// dirty but not committed\n", encoding="utf-8")
            self.assertEqual({100}, set(promotion.load_accepted_contracts(
                root, ledger_path, manifest_path, base, final, base,
                promotion.canonical_digest(ledger))[0]))
            with self.assertRaisesRegex(promotion.PromotionError, "digest changed"):
                promotion.load_accepted_contracts(
                    root, ledger_path, manifest_path, base, final, base, "0" * 64)
            with self.assertRaisesRegex(promotion.PromotionError,
                                        "contiguous integration chain"):
                promotion.load_accepted_contracts(
                    root, ledger_path, manifest_path, base, state, base,
                    promotion.canonical_digest(ledger))
            helper = root / "LiteDB.Tests/Helper.cs"
            helper.write_text("// same tests, changed helper semantics\n", encoding="utf-8")
            self.git(root, "add", "LiteDB.Tests/Helper.cs")
            self.git(root, "commit", "--quiet", "-m", "change hidden test helper")
            after_chain = self.git(root, "rev-parse", "HEAD")
            with self.assertRaisesRegex(promotion.PromotionError,
                                        "changed frozen test/harness source"):
                promotion.load_accepted_contracts(
                    root, ledger_path, manifest_path, base, after_chain, base,
                    promotion.canonical_digest(ledger))


class EvidenceIdentityTests(unittest.TestCase):
    def test_final_capture_rejects_a_single_issue_or_stale_ledger_identity(self):
        expected = {"kind": "final-promotion", "final_integration_sha": FINAL,
                    "accepted_state_sha": "c" * 40,
                    "accepted_ledger_sha256": "d" * 64,
                    "test_source_sha": TEST_SOURCE}
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "evidence.json"
            path.write_text(json.dumps({"schema_version": 1, "accepted": True,
                                        "validation": {"kind": "single-issue", "issue": 2874}}))
            with self.assertRaisesRegex(promotion.PromotionError,
                                        "final-promotion identity changed"):
                promotion.load_evidence(path, "baseline", BASE, expected,
                                        {"sha256": "q", "coverage_gaps": [], "jobs": set()},
                                        "n", {}, "workflow")

    def test_each_required_test_or_repro_job_has_one_authenticated_artifact(self):
        expected = {"kind": "final-promotion", "final_integration_sha": FINAL,
                    "accepted_state_sha": "c" * 40,
                    "accepted_ledger_sha256": "d" * 64,
                    "test_source_sha": TEST_SOURCE}
        job = {"name": "one test job", "kind": "tests", "status": "completed",
               "conclusion": "success", "artifact_id": 10,
               "artifact_sha256": "e" * 64,
               "tests": [result("one", CONTROL, "passed")]}
        value = {"schema_version": 1, "accepted": True, "validation": expected,
                 "source_sha": BASE, "role": "baseline",
                 "failure_normalization_sha256": "n", "quarantine_sha256": "q",
                 "coverage_gaps": [], "evidence_definition_sha": "f" * 40,
                 "run": {"id": 1, "head_sha": "f" * 40,
                         "workflow_path": "workflow", "event": "workflow_dispatch",
                         "status": "completed", "conclusion": "success"},
                 "jobs": [job]}
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "evidence.json"
            path.write_text(json.dumps(value))
            with patch.object(promotion, "EXPECTED_JOBS", 1), \
                    patch.object(promotion, "EXPECTED_ARTIFACTS", 1), \
                    patch.object(promotion, "OVERLAY_REPROS", {}):
                loaded, jobs = promotion.load_evidence(
                    path, "baseline", BASE, expected,
                    {"sha256": "q", "coverage_gaps": [], "jobs": set()},
                    "n", {}, "workflow")
                self.assertEqual(1, loaded["run"]["id"])
                self.assertEqual({"one test job"}, set(jobs))
                del value["jobs"][0]["artifact_id"]
                path.write_text(json.dumps(value))
                with self.assertRaisesRegex(promotion.PromotionError,
                                            "authenticated artifact"):
                    promotion.load_evidence(
                        path, "baseline", BASE, expected,
                        {"sha256": "q", "coverage_gaps": [], "jobs": set()},
                        "n", {}, "workflow")


if __name__ == "__main__":
    unittest.main()
