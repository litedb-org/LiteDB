"""Actual downloaded payloads, rather than hand-asserted event outcomes, gate acceptance."""

import hashlib
import io
import json
import unittest
import zipfile
from unittest.mock import patch

from artifacts import MAX_REPORT, download, validate
from state import Rejected
from test_worker_runtime import runtime_fixture


def archive(files):
    output = io.BytesIO()
    with zipfile.ZipFile(output, "w") as zipped:
        for name, report in files.items():
            member = zipfile.ZipInfo("placeholder")
            member.filename = name
            zipped.writestr(member, report if isinstance(report, bytes) else json.dumps(report).encode())
    return output.getvalue()


class ArtifactTests(unittest.TestCase):
    def setUp(self):
        self.event = {
            "schema_version": 1, "issue": 2874, "base_sha": "a" * 40,
            "candidate_sha": "d" * 40, "test_source_sha": "b" * 40,
            "workflow_sha": "c" * 40, "environment": "linux-x64-net8.0",
            "kind": "focused", "outcome": "behavior_correct", "run_id": 123,
        }
        self.verdict = {key: value for key, value in self.event.items() if key not in ("kind", "run_id")}
        self.verdict.update(accepted=True, level="focused")
        self.scope = {"accepted": True, "outcome": "scope_verified",
                      "provenance": {key: self.event[key] for key in ("issue", "base_sha", "candidate_sha")}}
        self.files = {"verdict.json": self.verdict, "scope.json": self.scope}

    def check(self):
        return validate(archive(self.files), self.event)

    def review(self):
        self.event.update(kind="review", role="behavior", outcome="pass", findings=[])
        fields = ("schema_version", "issue", "base_sha", "candidate_sha", "test_source_sha", "role")
        result = {key: self.event[key] for key in fields}
        result.update(verdict="pass", findings=[], coverage=["Checked null handling and valid inputs"])
        metadata = {key: self.event[key] for key in fields}
        metadata.update(workflow_sha=self.event["workflow_sha"], kind="review", run_id="123")
        runtime, proof = runtime_fixture("gpt-5.6-sol")
        metadata.update(runtime)
        metadata["result_sha256"] = hashlib.sha256(json.dumps(result).encode()).hexdigest()
        self.files = {"result.json": result, "metadata.json": metadata, "runtime-proof.json": proof}
        return result, metadata

    def test_positive_payload_returns_computed_report_and_archive_digests(self):
        hashes = self.check()
        self.assertEqual(hashlib.sha256(archive(self.files)).hexdigest(), hashes["artifact_sha256"])
        self.assertEqual(hashlib.sha256(json.dumps(self.verdict).encode()).hexdigest(), hashes["report_sha256"])

    def test_nits_remain_authenticated_in_approved_artifact(self):
        result, metadata = self.review()
        nit = {"severity": "nit", "summary": "Optional wording", "path": "source.cs", "evidence": "Comment diff"}
        result["findings"] = [nit]
        self.event["findings"] = [nit]
        metadata["result_sha256"] = hashlib.sha256(json.dumps(result).encode()).hexdigest()
        self.check()
        self.event["findings"] = []
        with self.assertRaisesRegex(Rejected, "findings differ"):
            self.check()

    def test_missing_rejected_or_forged_verdict_cannot_approve(self):
        for field, value in (("accepted", False), ("accepted", 1), ("outcome", "harness_error"),
                             ("candidate_sha", "e" * 40), ("test_source_sha", "e" * 40),
                             ("workflow_sha", "e" * 40), ("issue", "2874"),
                             ("environment", "windows-x64-net8.0"), ("level", "broad")):
            with self.subTest(field=field, value=value):
                original = self.verdict[field]
                self.verdict[field] = value
                with self.assertRaises(Rejected):
                    self.check()
                self.verdict[field] = original
        del self.files["verdict.json"]
        with self.assertRaises(Rejected):
            self.check()

    def test_scope_is_required_even_with_passing_verdict(self):
        for field, value in (("accepted", False), ("outcome", "tests_unchanged"),
                             ("provenance", {**self.scope["provenance"], "base_sha": "e" * 40})):
            with self.subTest(field=field):
                original = self.scope[field]
                self.scope[field] = value
                with self.assertRaises(Rejected):
                    self.check()
                self.scope[field] = original
        del self.files["scope.json"]
        with self.assertRaises(Rejected):
            self.check()

    def test_baseline_requires_null_candidate_and_expected_bug(self):
        self.event.update(kind="baseline", candidate_sha=None, outcome="bug_present")
        self.verdict.update(level="baseline", candidate_sha=None, outcome="bug_present")
        del self.files["scope.json"]
        self.check()
        self.verdict["outcome"] = "behavior_correct"
        with self.assertRaises(Rejected):
            self.check()

    def test_broad_and_acceptance_map_behavior_correct_to_pass(self):
        for kind in ("broad", "acceptance"):
            self.event.update(kind=kind, outcome="pass")
            self.verdict["level"] = kind
            self.check()

    def test_independent_review_payload_and_metadata_pass(self):
        self.review()
        self.check()

    def test_review_cannot_approve_without_runtime_proof(self):
        self.review()
        del self.files["runtime-proof.json"]
        with self.assertRaisesRegex(Rejected, "Missing or oversized runtime proof"):
            self.check()

    def test_stale_review_empty_coverage_or_findings_cannot_approve(self):
        result, _ = self.review()
        for field, value in (("candidate_sha", "e" * 40), ("role", "lifecycle"),
                             ("verdict", "changes_requested"), ("findings", ["defect"]),
                             ("coverage", []), ("coverage", [""])):
            with self.subTest(field=field):
                original = result[field]
                result[field] = value
                with self.assertRaises(Rejected):
                    self.check()
                result[field] = original

    def test_review_digest_or_run_mismatch_rejected(self):
        _, metadata = self.review()
        for field, value in (("result_sha256", "wrong"), ("run_id", "124"), ("workflow_sha", "e" * 40)):
            with self.subTest(field=field):
                original = metadata[field]
                metadata[field] = value
                with self.assertRaises(Rejected):
                    self.check()
                metadata[field] = original

    def test_archive_paths_and_symlinks_cannot_escape(self):
        for filename in ("../outside", "/absolute", "C:/absolute", "nested\\outside"):
            with self.subTest(filename=filename):
                self.files[filename] = b"unexpected"
                with self.assertRaises(Rejected):
                    self.check()
                del self.files[filename]
        data = io.BytesIO(archive(self.files))
        with zipfile.ZipFile(data, "a") as zipped:
            link = zipfile.ZipInfo("link")
            link.external_attr = 0o120777 << 16
            zipped.writestr(link, "target")
        with self.assertRaises(Rejected):
            validate(data.getvalue(), self.event)

    def test_invalid_and_oversized_report_rejected(self):
        for content in (b"not-json", b"[]", b" " * (MAX_REPORT + 1)):
            self.files["verdict.json"] = content
            with self.assertRaises(Rejected):
                self.check()
        with self.assertRaises(Rejected):
            validate(b"not-a-zip", self.event)

    def test_download_rejects_unbounded_artifact_before_request(self):
        with patch("artifacts.subprocess.run") as command:
            with self.assertRaises(Rejected):
                download("owner/repo", {"id": 123, "size_in_bytes": 1000000000})
        command.assert_not_called()


if __name__ == "__main__":
    unittest.main()
