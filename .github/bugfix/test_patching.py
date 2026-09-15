"""Worker provenance and patch integrity before candidate code can be published."""

import hashlib
import json
import unittest

from patching import worker_payload
from state import Rejected, new_state
from test_artifacts import archive


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

    def check(self):
        data = archive({"result.json": self.result, "metadata.json": self.metadata, "patch.diff": self.patch})
        return worker_payload(data, self.state, "a" * 40, 123)

    def test_valid_fix_artifact(self):
        self.assertEqual(self.patch, self.check()[0])

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


if __name__ == "__main__":
    unittest.main()
