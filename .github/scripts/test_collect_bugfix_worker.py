#!/usr/bin/env python3
"""Check worker collection against isolated Git repositories and forged results."""

from __future__ import annotations

import hashlib
import json
import subprocess
import tempfile
import unittest
from pathlib import Path

import collect_bugfix_worker as worker


class CollectorTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        root = Path(self.temp.name)
        self.repo = root / "repo"
        self.repo.mkdir()
        self.output = root / "output"
        self.output.mkdir()
        self.manifest_path = root / "issues.json"
        self.command("init", "--quiet")
        self.command("config", "user.name", "Worker test")
        self.command("config", "user.email", "worker-test@example.invalid")
        self.command("config", "core.autocrlf", "false")
        (self.repo / "source.cs").write_text("old behavior\n")
        (self.repo / "test.cs").write_text("frozen assertion\n")
        (self.repo / "other.cs").write_text("unrelated code\n")
        self.command("add", ".")
        self.command("commit", "--quiet", "-m", "Fixture")
        base = self.command("rev-parse", "HEAD").strip()
        blob = self.command("hash-object", "test.cs").strip()
        self.env = {
            "BUGFIX_ISSUE": "2874", "BUGFIX_BASE_SHA": base,
            "BUGFIX_TEST_SOURCE_SHA": base, "GITHUB_WORKFLOW_SHA": base,
            "GITHUB_RUN_ID": "123", "GITHUB_RUN_ATTEMPT": "1",
            "BUGFIX_MODEL": "gpt-6-astra",
            "BUGFIX_REASONING_EFFORT": "high",
        }
        self.manifest_path.write_text(json.dumps({
            "schema_version": 1,
            "issues": {"2874": {
                "frozen_test_revision": base,
                "frozen_test_blobs": {"test.cs": blob},
                "allowed_production_paths": ["source.cs"],
            }},
        }))
        self.result = {
            **worker.identity(self.env), "status": "proposed",
            "summary": "Reject invalid values before reading bytes",
            "tests": ["Static inspection only; CI still required"],
        }

    def command(self, *args):
        return subprocess.check_output(["git", "-C", str(self.repo), *args], text=True)

    def collect(self):
        (self.output / "result.json").write_text(json.dumps(self.result))
        return worker.collect(self.repo, self.manifest_path, self.output, self.env)

    def modify_source(self):
        (self.repo / "source.cs").write_text("correct behavior\n")

    def review(self):
        self.env["BUGFIX_CANDIDATE_SHA"] = self.env["BUGFIX_BASE_SHA"]
        self.env["BUGFIX_ROLE"] = "behavior"
        self.env["BUGFIX_MODEL"] = "gpt-5.6-sol"
        self.result = {
            **worker.identity(self.env), "verdict": "pass", "findings": [],
            "coverage": ["Inspected source.cs valid-value path and frozen assertion"],
        }

    def test_collects_restricted_patch_with_digest_and_identity(self):
        self.modify_source()
        metadata = self.collect()
        patch = (self.output / "patch.diff").read_bytes()
        self.assertIn(b"+correct behavior", patch)
        self.assertEqual(hashlib.sha256(patch).hexdigest(), metadata["patch_sha256"])
        self.assertEqual(["source.cs"], metadata["changed_paths"])
        self.assertEqual(self.env["BUGFIX_BASE_SHA"], metadata["base_sha"])

    def test_rejects_empty_patch(self):
        with self.assertRaisesRegex(ValueError, "empty patch"):
            self.collect()

    def test_rejects_model_or_reasoning_downgrade(self):
        self.modify_source()
        self.env["BUGFIX_REASONING_EFFORT"] = "medium"
        with self.assertRaisesRegex(ValueError, "reasoning must be high"):
            self.collect()

    def test_rejects_changed_frozen_test(self):
        self.modify_source()
        (self.repo / "test.cs").write_text("weakened assertion\n")
        with self.assertRaisesRegex(ValueError, "Frozen regression changed"):
            self.collect()

    def test_rejects_forbidden_production_edit(self):
        self.modify_source()
        (self.repo / "other.cs").write_text("unrelated edit\n")
        with self.assertRaisesRegex(ValueError, "forbidden path"):
            self.collect()

    def test_rejects_added_and_staged_untracked_file(self):
        self.modify_source()
        (self.repo / "new.cs").write_text("extra code\n")
        with self.assertRaisesRegex(ValueError, "untracked"):
            self.collect()
        self.command("add", "new.cs")
        with self.assertRaisesRegex(ValueError, "forbidden path"):
            self.collect()

    def test_rejects_changed_head(self):
        self.modify_source()
        self.command("add", "source.cs")
        self.command("commit", "--quiet", "-m", "Unexpected worker commit")
        with self.assertRaisesRegex(ValueError, "changed HEAD"):
            self.collect()

    def test_rejects_stale_identity(self):
        self.modify_source()
        self.result["base_sha"] = "0" * 40
        with self.assertRaisesRegex(ValueError, "identity mismatch"):
            self.collect()

    def test_rejects_deleted_production_file(self):
        (self.repo / "source.cs").unlink()
        with self.assertRaisesRegex(ValueError, "forbidden path"):
            self.collect()

    def test_review_collects_clean_workspace_and_exact_role(self):
        self.review()
        self.assertEqual("behavior", self.collect()["role"])
        self.assertFalse((self.output / "patch.diff").exists())

    def test_review_rejects_code_changes(self):
        self.review()
        self.modify_source()
        with self.assertRaisesRegex(ValueError, "changed tracked"):
            self.collect()

    def test_review_rejects_pass_with_unresolved_finding(self):
        self.review()
        self.result["findings"] = [{"summary": "A defect", "path": "source.cs", "evidence": "Invalid value still read"}]
        with self.assertRaisesRegex(ValueError, "disagree"):
            self.collect()

    def test_review_rejects_missing_coverage_and_forged_role(self):
        self.review()
        self.result["coverage"] = []
        with self.assertRaisesRegex(ValueError, "nonempty list"):
            self.collect()
        self.result["coverage"] = ["Actual inspection"]
        self.result["role"] = "compatibility"
        with self.assertRaisesRegex(ValueError, "identity mismatch"):
            self.collect()


if __name__ == "__main__":
    unittest.main()
