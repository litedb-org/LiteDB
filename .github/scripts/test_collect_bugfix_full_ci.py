import argparse
import hashlib
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import xml.etree.ElementTree as ET
import zipfile

import collect_bugfix_full_ci as collector


SOURCE = "a" * 40
DEFINITION = "b" * 40
TEST_NAME = "LiteDB.Tests.Issues.Issue2874_Tests.Invalid(value: 1)"


def zipped(files):
    output = io.BytesIO()
    with zipfile.ZipFile(output, "w") as archive:
        for name, value in files.items():
            archive.writestr(name, value)
    return output.getvalue()


def trx():
    root = ET.Element("TestRun")
    definitions = ET.SubElement(root, "TestDefinitions")
    definition = ET.SubElement(definitions, "UnitTest", id="1", name=TEST_NAME)
    ET.SubElement(definition, "TestMethod",
                  className="LiteDB.Tests.Issues.Issue2874_Tests")
    results = ET.SubElement(root, "Results")
    result = ET.SubElement(results, "UnitTestResult", testName=TEST_NAME,
                           outcome="Failed", testId="1", executionId="execution-1")
    message = ET.SubElement(ET.SubElement(ET.SubElement(result, "Output"),
                                          "ErrorInfo"), "Message")
    message.text = "Expected ArgumentException\nSystem.IndexOutOfRangeException\n   at Source()"
    entries = ET.SubElement(root, "TestEntries")
    ET.SubElement(entries, "TestEntry", testId="1", executionId="execution-1")
    summary = ET.SubElement(root, "ResultSummary", outcome="Failed")
    ET.SubElement(summary, "Counters", total="1", passed="0", failed="1",
                  executed="1", completed="1", notExecuted="0")
    return ET.tostring(root)


def repro_report(failed=True):
    variant = {"Expected": 0, "ExpectedExitCode": 0,
               "ExpectedLogContains": "BUG_2854_CONFIRMED", "Actual": 1,
               "Met": False, "ExitCode": 1, "UseProjectReference": True,
               "FailureReason": "Expected output containing BUG_2854_CONFIRMED"}
    return json.dumps({"Repros": [{"Id": "Issue_2854_CircularPageList", "State": 0,
                                   "Failed": failed, "Warned": False,
                                   "Package": variant, "Latest": variant}]})


class FullCiCollectorTests(unittest.TestCase):
    def test_final_promotion_identity_binds_all_accepted_ledger_inputs(self):
        args = argparse.Namespace(
            validation_scope="final-promotion", issue=None,
            final_integration_sha="a" * 40, accepted_state_sha="b" * 40,
            accepted_ledger_sha256="c" * 64, test_source_sha="d" * 40)
        self.assertEqual({
            "kind": "final-promotion",
            "final_integration_sha": "a" * 40,
            "accepted_state_sha": "b" * 40,
            "accepted_ledger_sha256": "c" * 64,
            "test_source_sha": "d" * 40,
        }, collector.validation_identity(args))
        args.issue = 2874
        with self.assertRaisesRegex(collector.CollectionError,
                                    "cannot name one issue"):
            collector.validation_identity(args)

    def test_single_issue_identity_rejects_final_promotion_fields(self):
        args = argparse.Namespace(
            validation_scope="single-issue", issue=2874,
            final_integration_sha="", accepted_state_sha="",
            accepted_ledger_sha256="", test_source_sha="")
        self.assertEqual({"kind": "single-issue", "issue": 2874},
                         collector.validation_identity(args))
        args.accepted_state_sha = "a" * 40
        with self.assertRaisesRegex(collector.CollectionError,
                                    "cannot carry final-promotion"):
            collector.validation_identity(args)

    def test_maps_every_original_test_job_shape_to_an_artifact(self):
        expected = {
            "build-and-test / Test (Linux .NET 8)": "bugfix-full-ci-tests-linux-net8",
            "build-and-test / Test (Linux ARM64 - .NET 10)":
                "bugfix-full-ci-tests-linux-arm64-net10",
            "build-and-test / Test (macOS .NET 9)": "bugfix-full-ci-tests-macos-net9",
            "build-and-test / Test (Windows windows-2022 - x86 - .NET 8)":
                "bugfix-full-ci-tests-windows-2022-x86-net8",
            "build-and-test / Cross-Process Tests (Windows windows-latest - x64 - .NET 10)":
                "bugfix-full-ci-crossprocess-windows-latest-x64-net10",
        }
        for job, artifact in expected.items():
            with self.subTest(job=job):
                self.assertEqual(artifact, collector.artifact_for_job(job))

    def test_collects_self_contained_evidence_and_preserves_raw_inputs(self):
        run_id = 42
        quarantine = {
            "schema_version": 1, "expected_original_jobs": 6, "expected_remaining_jobs": 3,
            "quarantines": [{"kind": "repro", "issue": 2854,
                             "repro": "Issue_2854_CircularPageList", "status": "unverified",
                             "authorized_on": "2026-09-15", "authorization": "test",
                             "reason": "missing helper",
                             "jobs": [
                                 "repro-runner / Run Issue_2854_CircularPageList on one",
                                 "repro-runner / Run Issue_2854_CircularPageList on two",
                                 "repro-runner / Run Issue_2854_CircularPageList on three"]}]}
        quarantine_raw = json.dumps(quarantine).encode()
        test_job = {"id": 2, "name": "build-and-test / Test (Linux .NET 8)",
                    "status": "completed", "conclusion": "failure", "html_url": "test"}
        repro_job = {"id": 3,
                     "name": "repro-runner / Run Issue_2854_CircularPageList on ubuntu-22.04",
                     "status": "completed", "conclusion": "failure", "html_url": "repro"}
        jobs = [{"id": 1, "name": "build-and-test / Build (Linux)",
                 "status": "completed", "conclusion": "success", "html_url": "build"},
                test_job, repro_job]
        overlay_manifest = Path(".github/bugfix/issue-2794-harness-overlay.json")
        overlay_manifest_sha = hashlib.sha256(overlay_manifest.read_bytes()).hexdigest()
        issue_2825_overlay_manifest = Path(".github/bugfix/issue-2825-harness-overlay.json")
        issue_2825_overlay_manifest_sha = hashlib.sha256(
            issue_2825_overlay_manifest.read_bytes()).hexdigest()
        provenance = {"schema_version": 3, "issue": 2874, "source_sha": SOURCE,
                      "checkout_sha": SOURCE, "evidence_definition_sha": DEFINITION,
                      "run_id": run_id,
                      "quarantine_sha256": hashlib.sha256(quarantine_raw).hexdigest(),
                      "harness_overlay_manifest_sha256": overlay_manifest_sha,
                      "issue_2825_harness_overlay_manifest_sha256":
                          issue_2825_overlay_manifest_sha}
        payloads = {
            "bugfix-full-ci-provenance": zipped(
                {"full-ci-provenance.json": json.dumps(provenance)}),
            "bugfix-full-ci-tests-linux-net8": zipped({
                "discovery.txt": "The following Tests are available:\n    " + TEST_NAME + "\n",
                "TestResults.trx": trx(),
            }),
            "logs-Issue_2854_CircularPageList-ubuntu-22.04": zipped(
                {"artifacts/repro-report.json": repro_report(),
                 "artifacts/repro-console.log":
                     "FAIL: Variant did not execute.\nerror CS0117: missing member\n"}),
        }
        artifacts = [{"id": index, "name": name, "expired": False,
                      "size_in_bytes": len(payloads[name])}
                     for index, name in enumerate(payloads, 10)]
        by_id = {artifact["id"]: payloads[artifact["name"]] for artifact in artifacts}
        run = {"id": run_id, "status": "completed", "path": collector.WORKFLOW_PATH,
               "event": "workflow_dispatch", "head_sha": DEFINITION,
               "display_title": "decorative title",
               "conclusion": "failure", "html_url": "run"}

        def fake_api(_repository, resource):
            if resource == f"actions/runs/{run_id}":
                return run
            if "/jobs?" in resource:
                return {"total_count": len(jobs), "jobs": jobs}
            if "/artifacts?" in resource:
                return {"total_count": len(artifacts), "artifacts": artifacts}
            raise AssertionError(resource)

        with tempfile.TemporaryDirectory() as directory:
            quarantine_path = Path(directory) / "quarantine-input.json"
            quarantine_path.write_bytes(quarantine_raw)
            failure_policy = Path(directory) / "failure-normalization.json"
            failure_policy.write_text('{"schema_version":1,"tests":{}}', encoding="utf-8")
            args = argparse.Namespace(repository="owner/repo", run_id=run_id, issue=2874,
                                      source_sha=SOURCE, evidence_definition_sha=DEFINITION,
                                      control_root=".", archive_dir=directory,
                                      quarantine=str(quarantine_path), role="baseline",
                                      failure_normalization=str(failure_policy),
                                      harness_overlay_manifest=str(overlay_manifest),
                                      issue_2825_harness_overlay_manifest=
                                          str(issue_2825_overlay_manifest))
            with patch.object(collector, "CHECK_JOBS", {jobs[0]["name"]}), \
                    patch.object(collector, "EXPECTED_TEST_JOBS", 1), \
                    patch.object(collector, "api_json", side_effect=fake_api), \
                    patch.object(collector, "artifact_bytes",
                                 side_effect=lambda _repo, item: by_id[item["id"]]):
                evidence = collector.collect(args)
            self.assertTrue(evidence["accepted"])
            self.assertEqual("failed", evidence["jobs"][1]["tests"][0]["outcome"])
            self.assertEqual("unmeasured", evidence["jobs"][1]["runtime_architecture"])
            self.assertEqual(hashlib.sha256(failure_policy.read_bytes()).hexdigest(),
                             evidence["failure_normalization_sha256"])
            self.assertEqual(overlay_manifest_sha,
                             evidence["harness_overlay_manifest_sha256"])
            self.assertEqual("issue-2794-per-wait-deadline",
                             evidence["harness_overlay"]["id"])
            self.assertEqual("issue-2825-data-insert-secondary-failure",
                             evidence["issue_2825_harness_overlay"]["id"])
            self.assertEqual("harness_error", evidence["jobs"][2]["verdict"])
            self.assertIn("CS0117", "\n".join(evidence["jobs"][2]["diagnostics"]))
            self.assertIn("package: Expected output containing BUG_2854_CONFIRMED",
                          evidence["jobs"][2]["diagnostics"])
            archive = Path(directory) / str(run_id)
            self.assertTrue((archive / "run.json").is_file())
            self.assertEqual(3, len(list(archive.glob("*.zip"))))

    def test_rejects_incomplete_discovery(self):
        with self.assertRaisesRegex(collector.CollectionError, "complete test discovery"):
            collector.discovery_names(b"no inventory here")

    def test_requires_exact_issue_2794_overlay_provenance(self):
        manifest, manifest_sha = collector.load_harness_overlay(
            ".github/bugfix/issue-2794-harness-overlay.json")
        target_os = "windows-2022"
        provenance = collector.harness_overlay_contract(
            manifest, manifest_sha, SOURCE, DEFINITION, target_os)
        variant = {"Expected": 1, "ExpectedExitCode": 10,
                   "ExpectedLogContains": "VERIFIED_2794", "Actual": 1,
                   "Met": True, "ExitCode": 10, "UseProjectReference": True,
                   "FailureReason": None}
        report = {"Repros": [{"Id": "Issue_2794_SharedJobHandoff", "State": 1,
                               "Failed": False, "Warned": False,
                               "Package": variant, "Latest": variant}]}
        job = {"id": 1,
               "name": f"repro-runner / Run Issue_2794_SharedJobHandoff on {target_os}",
               "status": "completed", "conclusion": "success", "html_url": "job"}
        artifact = {"id": 2, "name": "logs-Issue_2794-windows-2022"}

        def payload(record):
            return zipped({
                "artifacts/repro-report.json": json.dumps(report),
                "artifacts/repro-console.log": "VERIFIED_2794\n",
                "artifacts/harness-overlay-provenance.json": json.dumps(record),
            })

        overlays = {"Issue_2794_SharedJobHandoff": (
            manifest, manifest_sha, collector.harness_overlay_contract)}
        result = collector.repro_job(
            job, payload(provenance), artifact, overlays, SOURCE, DEFINITION)
        self.assertEqual(provenance, result["harness_overlay"])
        changed = dict(provenance, effective_git_blob_sha1="d" * 40)
        with self.assertRaisesRegex(collector.CollectionError, "trusted definition"):
            collector.repro_job(
                job, payload(changed), artifact, overlays, SOURCE, DEFINITION)

    def test_requires_exact_issue_2825_overlay_provenance(self):
        manifest, manifest_sha = collector.load_issue_2825_harness_overlay(
            ".github/bugfix/issue-2825-harness-overlay.json")
        target_os = "ubuntu-22.04"
        provenance = collector.issue_2825_harness_overlay_contract(
            manifest, manifest_sha, SOURCE, DEFINITION, target_os)
        variant = {"Expected": 1, "ExpectedExitCode": 10,
                   "ExpectedLogContains": "VERIFIED_2825", "Actual": 1,
                   "Met": True, "ExitCode": 10, "UseProjectReference": True,
                   "FailureReason": None}
        report = {"Repros": [{"Id": "Issue_2825_FreeListRace", "State": 1,
                               "Failed": False, "Warned": False,
                               "Package": variant, "Latest": variant}]}
        job = {"id": 1,
               "name": f"repro-runner / Run Issue_2825_FreeListRace on {target_os}",
               "status": "completed", "conclusion": "success", "html_url": "job"}
        artifact = {"id": 2, "name": "logs-Issue_2825-ubuntu-22.04"}

        def payload(record):
            return zipped({
                "artifacts/repro-report.json": json.dumps(report),
                "artifacts/repro-console.log": "VERIFIED_2825\n",
                "artifacts/harness-overlay-provenance.json": json.dumps(record),
            })

        overlays = {"Issue_2825_FreeListRace": (
            manifest, manifest_sha, collector.issue_2825_harness_overlay_contract)}
        result = collector.repro_job(
            job, payload(provenance), artifact, overlays, SOURCE, DEFINITION)
        self.assertEqual(provenance, result["harness_overlay"])
        with self.assertRaisesRegex(collector.CollectionError, "trusted definition"):
            collector.repro_job(
                job, payload(dict(provenance, effective_blob_sha256="d" * 64)), artifact,
                overlays, SOURCE, DEFINITION)

    def test_preserves_duplicate_rendered_discovery_names(self):
        raw = ("The following Tests are available:\n"
               f"    {TEST_NAME}\n    {TEST_NAME}\n").encode()
        self.assertEqual([TEST_NAME, TEST_NAME], collector.discovery_names(raw))

    def test_rejects_duplicate_archive_members(self):
        output = io.BytesIO()
        with zipfile.ZipFile(output, "w") as archive:
            archive.writestr("results.trx", "first")
            archive.writestr("results.trx", "second")
        with self.assertRaisesRegex(collector.CollectionError, "Duplicate artifact ZIP member"):
            collector.zip_members(output.getvalue())

    def test_rejects_artifact_metadata_outside_download_bound(self):
        artifact = {"id": 1, "name": "oversize",
                    "size_in_bytes": collector.MAX_ARCHIVE + 1}
        with self.assertRaisesRegex(collector.CollectionError, "download bound"):
            collector.artifact_bytes("owner/repo", artifact)


if __name__ == "__main__":
    unittest.main()
