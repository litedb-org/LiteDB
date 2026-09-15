"""Real compiler errors route to repair; incomplete/dependency evidence never does."""

import hashlib
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

from build_failures import production_failure, test_build_failure
from compiler_feedback import MAX_DIAGNOSTICS, classify, write_build_report
from evidence import check_event, event_for
from feedback import repair_feedback
from state import Rejected, apply_event, new_state
from test_artifacts import archive
from test_profiles import profile_fixture


SOURCE = "LiteDB/Document/ObjectId.cs"
RAW = (f"/checkout/candidate/{SOURCE}(30,15): error CS0103: The name 'missing' does not exist "
       "in the current context [/checkout/candidate/LiteDB/LiteDB.csproj::TargetFramework=net8.0]\n").encode()


class CompilerClassificationTests(unittest.TestCase):
    def test_located_linux_and_windows_errors_are_bounded_deduplicated(self):
        linux = classify(RAW + RAW, "/checkout/candidate", 1)
        self.assertEqual("compiler_error", linux["outcome"])
        self.assertEqual(1, len(linux["diagnostics"]))
        self.assertEqual(SOURCE, linux["diagnostics"][0]["path"])
        windows = RAW.replace(b"/checkout/candidate", b"D:/a/LiteDB/candidate").replace(b"/", b"\\")
        self.assertEqual(linux, classify(windows, "D:\\a\\LiteDB\\candidate", 1))

    def test_dependency_timeout_unlocated_external_and_mixed_errors_are_infrastructure(self):
        examples = [RAW.replace(b"CS0103", b"CS0012"), RAW + b"error NU1301: feed unavailable\n",
                    RAW + b"error MSB3021: cannot copy assembly\n", b"CSC : error CS0006: missing metadata\n",
                    RAW.replace(b"/checkout/candidate/", b"/elsewhere/"), b"Restore timed out\n"]
        for raw in examples:
            with self.subTest(raw=raw):
                self.assertEqual("harness_error", classify(raw, "/checkout/candidate", 1)["outcome"])
        for code, timeout in ((1, True), (137, False), (0, False), (None, False), (True, False)):
            self.assertEqual("harness_error", classify(RAW, "/checkout/candidate", code, timeout)["outcome"])

    def test_too_many_errors_fail_closed_and_long_message_is_bounded(self):
        many = b"".join(RAW.replace(b"(30,15)", f"({line},15)".encode()) for line in range(MAX_DIAGNOSTICS + 1))
        self.assertEqual("harness_error", classify(many, "/checkout/candidate", 1)["outcome"])
        long = RAW.replace(b"missing", b"x" * 5000)
        self.assertEqual(2000, len(classify(long, "/checkout/candidate", 1)["diagnostics"][0]["message"]))


class CompilerEvidenceTests(unittest.TestCase):
    def setUp(self):
        self.state = new_state("canary", 2874, "a" * 40, "b" * 40, "c" * 40)
        profile = profile_fixture()
        profile["paths"] = [SOURCE]
        self.state.update(candidate_sha="d" * 40, phase="broad", repair_attempts=1, protocol="compressed-v1",
                          acceptance_profile=profile, orchestration={"review_reports": {}})
        self.identity = {"issue": 2874, "source_sha": "d" * 40, "test_source_sha": "b" * 40,
                         "workflow_sha": "c" * 40, "build_kind": "test", "framework": "net8.0"}
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            log = root / "build.log"
            log.write_bytes(RAW)
            self.report = write_build_report(root / "report.json", log, Path("/checkout/candidate"), self.identity, 1)
        # Test fixtures may run on Windows; the archive identifies its actual Linux runner root.
        self.report["repository_root"] = "/checkout/candidate"
        self.report.update(classify(RAW, "/checkout/candidate", 1))
        self.baseline = {**self.report, "source_sha": "a" * 40, "returncode": 0, "outcome": "success", "diagnostics": []}
        self.scope = {"accepted": True, "outcome": "scope_verified", "provenance":
                      {key: self.state[key] for key in ("issue", "base_sha", "candidate_sha")}}

    def data(self):
        return archive({"candidate/build-report.json": self.report, "candidate/build.log": RAW,
                        "baseline/build-report.json": self.baseline, "scope.json": self.scope})

    def test_candidate_compile_error_transitions_to_repair_with_actionable_feedback(self):
        outcome, diagnostics = test_build_failure(self.data(), self.state)
        self.assertEqual("fail", outcome)
        event = event_for(self.state, "broad", 42, outcome=outcome, diagnostics=diagnostics,
                          artifact="bugfix-check-ubuntu-latest-net8.0", environment="linux-x64-net8.0")
        result = apply_event(self.state, event)
        self.assertEqual("repairing", result["phase"])
        feedback = json.loads(repair_feedback(result))
        error = feedback["checks"][0]["diagnostics"][0]
        self.assertEqual((SOURCE, "CS0103", 30), (error["path"], error["code"], error["line"]))

    def test_stale_source_wrong_scope_failed_baseline_and_forged_log_do_not_repair(self):
        for field, value in (("source_sha", "0" * 40), ("workflow_sha", "0" * 40), ("log_sha256", "0" * 64)):
            original = self.report[field]
            self.report[field] = value
            self.assertEqual("harness_error", test_build_failure(self.data(), self.state)[0])
            self.report[field] = original
        self.baseline["returncode"] = 1
        self.assertEqual("harness_error", test_build_failure(self.data(), self.state)[0])
        self.baseline["returncode"] = 0
        self.state["acceptance_profile"]["paths"] = ["LiteDB/Other.cs"]
        self.assertEqual("harness_error", test_build_failure(self.data(), self.state)[0])

    def test_production_job_error_uses_same_authenticated_compiler_evidence(self):
        self.report.update(build_kind="production", framework="all-production-targets")
        data = archive({"production-build-report.json": self.report, "production-build.log": RAW})
        artifact = {"id": 42, "name": "bugfix-production", "expired": False}
        jobs = {"total_count": 1, "jobs": [{"name": "production", "status": "completed", "conclusion": "failure"}]}
        with patch("build_failures.download", return_value=data), patch("build_failures.github", return_value=jobs):
            result = production_failure("owner/repo", self.state, 7, [artifact])
            self.assertEqual("fail", result["outcome"])
            self.assertEqual("production", result["diagnostics"][0]["build_kind"])
            self.assertEqual(hashlib.sha256(data).hexdigest(), result["artifact_sha256"])
            jobs["jobs"][0]["conclusion"] = "cancelled"
            with self.assertRaises(Rejected):
                production_failure("owner/repo", self.state, 7, [artifact])

    def test_green_test_lane_and_production_compile_failure_route_to_repair(self):
        name = self.state["acceptance_profile"]["required_lanes"][0]
        event = event_for(self.state, "broad", 7, artifact=name, environment="linux-x64-net8.0")
        verdict = {key: event[key] for key in ("issue", "base_sha", "candidate_sha", "test_source_sha", "workflow_sha")}
        verdict.update(schema_version=1, level="broad", environment=event["environment"], accepted=True,
                       outcome="behavior_correct", protocol="compressed-v1", acceptance_profile=self.state["acceptance_profile"])
        data = archive({"verdict.json": verdict, "scope.json": self.scope})
        diagnostic = {"kind": "compiler_error", "build_kind": "production", "path": SOURCE, "code": "CS0103"}
        failure = {"artifact": "bugfix-production", "outcome": "fail", "diagnostics": [diagnostic],
                   "report_sha256": "1" * 64, "artifact_sha256": "2" * 64}
        with patch("evidence.download", return_value=data), patch("build_failures.production_failure", return_value=failure):
            result = check_event("owner/repo", self.state, {"id": 7, "conclusion": "failure"},
                                 [{"name": name, "expired": False}], None)
        self.assertEqual("fail", result["outcome"])
        self.assertEqual("repairing", apply_event(self.state, result)["phase"])
        self.assertEqual(diagnostic, result["diagnostics"][0]["diagnostics"][0])


if __name__ == "__main__":
    unittest.main()
