"""Worker provenance and patch integrity before candidate code can be published."""

import hashlib
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

from patching import CandidateConflict, commit_candidate, publish_candidate, worker_payload
from state import Rejected, new_state
from test_artifacts import archive
from test_worker_runtime import runtime_fixture


class PatchTests(unittest.TestCase):
    def setUp(self):
        self.state = new_state("canary", 2874, "a" * 40, "b" * 40, "c" * 40)
        self.result = {"schema_version": 1, "issue": 2874, "base_sha": "a" * 40,
                       "test_source_sha": "b" * 40, "status": "proposed", "summary": "Reject invalid byte windows",
                       "tests": ["Ran frozen argument validation tests"]}
        self.patch = b"a restricted patch"
        self.metadata = {key: self.result[key] for key in ("schema_version", "issue", "base_sha", "test_source_sha")}
        self.metadata.update(kind="fix", workflow_sha="c" * 40, run_id="123", changed_paths=["LiteDB/Document/ObjectId.cs"],
                             configured_model="gpt-6-astra", configured_reasoning_effort="high",
                             patch_sha256=hashlib.sha256(self.patch).hexdigest(),
                             result_sha256=hashlib.sha256(json.dumps(self.result).encode()).hexdigest())
        runtime, self.proof = runtime_fixture("gpt-6-astra")
        self.metadata.update(runtime)

    def check(self):
        data = archive({"result.json": self.result, "metadata.json": self.metadata,
                        "patch.diff": self.patch, "runtime-proof.json": self.proof})
        return worker_payload(data, self.state, "a" * 40, 123)

    def test_valid_fix_artifact(self):
        self.assertEqual(self.patch, self.check()[0])

    def test_fix_cannot_be_adopted_without_runtime_proof(self):
        data = archive({"result.json": self.result, "metadata.json": self.metadata, "patch.diff": self.patch})
        with self.assertRaisesRegex(Rejected, "Incomplete fix artifact"):
            worker_payload(data, self.state, "a" * 40, 123)

    def test_patch_and_report_digest_mismatches_rejected(self):
        self.patch = b"changed after collection"
        with self.assertRaisesRegex(Rejected, "patch_sha256"):
            self.check()
        self.patch = b"a restricted patch"
        self.result["summary"] = "changed after collection"
        with self.assertRaisesRegex(Rejected, "result_sha256"):
            self.check()

    def test_stale_source_and_lower_model_cannot_be_adopted(self):
        for field, value in (("base_sha", "d" * 40), ("workflow_sha", "d" * 40),
                             ("run_id", "124"), ("configured_model", "gpt-5.6-sol"),
                             ("configured_reasoning_effort", "medium"), ("reported_model", "other")):
            with self.subTest(field=field):
                original = self.metadata.get(field)
                self.metadata[field] = value
                with self.assertRaises(Rejected):
                    self.check()
                if original is None:
                    del self.metadata[field]
                else:
                    self.metadata[field] = original


class CandidatePublicationTests(unittest.TestCase):
    def test_candidate_commit_is_identical_when_recreated_later(self):
        with tempfile.TemporaryDirectory() as directory:
            repository = Path(directory)
            subprocess.run(["git", "init", "--quiet", repository], check=True)
            source = repository / "source.txt"
            source.write_text("base\n", encoding="utf-8")
            subprocess.run(["git", "-C", repository, "add", "source.txt"], check=True)
            subprocess.run(["git", "-C", repository, "-c", "user.name=base", "-c", "user.email=base@example.com",
                            "commit", "--quiet", "-m", "base"], check=True)
            parent = subprocess.check_output(["git", "-C", repository, "rev-parse", "HEAD"], text=True).strip()
            parent_date = subprocess.check_output(
                ["git", "-C", repository, "show", "-s", "--format=%ci", parent], text=True).strip()

            def create():
                source.write_text("candidate\n", encoding="utf-8")
                subprocess.run(["git", "-C", repository, "add", "source.txt"], check=True)
                return commit_candidate(repository, "Fix one reviewed defect")

            first = create()
            metadata = subprocess.check_output(
                ["git", "-C", repository, "show", "-s", "--format=%an%n%ae%n%ai%n%cn%n%ce%n%ci", first],
                text=True).splitlines()
            subprocess.run(["git", "-C", repository, "reset", "--hard", "--quiet", parent], check=True)
            second = create()
            self.assertEqual(first, second)
            self.assertEqual(["LiteDB bugfix controller", "bugfix-controller@users.noreply.github.com",
                              parent_date] * 2, metadata)

    def test_publish_requires_exact_existing_ref_or_exact_readback(self):
        exact = "d" * 40
        with patch("patching.git", side_effect=["", "", f"{exact}\trefs/heads/fix/issue-1-campaign-a1"]) as git:
            published = publish_candidate(Path("repo"), "owner/repo", "fix/issue-1-campaign-a1", exact)
        self.assertEqual(exact, published)
        self.assertEqual(3, git.call_count)

        with patch("patching.git", return_value=f"{'e' * 40}\trefs/heads/fix/issue-1-campaign-a1"):
            with self.assertRaisesRegex(CandidateConflict, "different commit"):
                publish_candidate(Path("repo"), "owner/repo", "fix/issue-1-campaign-a1", exact)

        with patch("patching.git", side_effect=["", "", f"{'e' * 40}\trefs/heads/fix/issue-1-campaign-a1"]):
            with self.assertRaisesRegex(CandidateConflict, "readback failed"):
                publish_candidate(Path("repo"), "owner/repo", "fix/issue-1-campaign-a1", exact)

        with patch("patching.git", return_value=f"{exact} refs/heads/wrong"):
            with self.assertRaisesRegex(CandidateConflict, "malformed"):
                publish_candidate(Path("repo"), "owner/repo", "fix/issue-1-campaign-a1", exact)


if __name__ == "__main__":
    unittest.main()
