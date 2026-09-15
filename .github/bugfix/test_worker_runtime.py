"""Fail closed on missing, downgraded, or mismatched worker runtime evidence."""

import hashlib
import json
import unittest

from artifacts import validate_worker_model
from state import Rejected


def runtime_fixture(model):
    proof = {"schema_version": 1, "codex_version": "0.154.0", "transport": "local_stub",
             "models": [{"model": name, "reasoning_effort": "high", "requests_checked": 1}
                        for name in ("gpt-6-astra", "gpt-5.6-sol")]}
    raw = json.dumps(proof).encode()
    metadata = {"configured_model": model, "configured_reasoning_effort": "high",
                "reported_model": model, "reported_reasoning_effort": None,
                "observed_request_models": [model], "observed_request_count": 1,
                "codex_version": "0.154.0", "verified_reasoning_effort": "high",
                "reasoning_verification": "local-request-capture",
                "runtime_proof_sha256": hashlib.sha256(raw).hexdigest()}
    return metadata, raw


class RuntimeEvidenceTests(unittest.TestCase):
    def setUp(self):
        self.metadata, self.raw = runtime_fixture("gpt-6-astra")

    def check(self):
        validate_worker_model(self.metadata, "fix", self.raw)

    def test_both_worker_roles_require_their_exact_observed_model(self):
        self.check()
        metadata, raw = runtime_fixture("gpt-5.6-sol")
        for role in ("behavior", "compatibility", "lifecycle"):
            validate_worker_model(metadata, role, raw)
        with self.assertRaises(Rejected):
            validate_worker_model(metadata, "fix", raw)

    def test_missing_or_downgraded_metadata_rejected(self):
        mutations = (("reported_model", None), ("observed_request_models", ["gpt-5.6-sol"]),
                     ("observed_request_models", ["gpt-6-astra", "gpt-5.6-sol"]),
                     ("observed_request_count", 0), ("observed_request_count", True),
                     ("observed_request_count", "1"), ("codex_version", "0.142.4"),
                     ("verified_reasoning_effort", "medium"),
                     ("reported_reasoning_effort", "medium"),
                     ("reasoning_verification", "configured-only"))
        for key, value in mutations:
            with self.subTest(key=key, value=value):
                original = self.metadata[key]
                self.metadata[key] = value
                with self.assertRaises(Rejected):
                    self.check()
                self.metadata[key] = original

    def test_raw_proof_digest_not_reserialized_digest_is_required(self):
        self.raw += b"\n"
        with self.assertRaisesRegex(Rejected, "digest mismatch"):
            self.check()

    def test_missing_proof_rejected(self):
        self.raw = None
        with self.assertRaisesRegex(Rejected, "Missing or oversized"):
            self.check()

    def test_wrong_proof_shape_version_or_model_rejected_even_with_matching_hash(self):
        proof = json.loads(self.raw)
        mutations = [[], {**proof, "schema_version": True}, {**proof, "codex_version": "0.142.4"},
                     {**proof, "transport": "configured-only"}, {**proof, "extra": 1}]
        for field, value in (("reasoning_effort", "medium"), ("requests_checked", 0),
                             ("requests_checked", True), ("model", "gpt-5.6-sol"), ("model", [])):
            mutations.append({**proof, "models": [{**proof["models"][0], field: value}, proof["models"][1]]})
        for changed in mutations:
            with self.subTest(proof=changed):
                self.raw = json.dumps(changed).encode()
                self.metadata["runtime_proof_sha256"] = hashlib.sha256(self.raw).hexdigest()
                with self.assertRaises(Rejected):
                    self.check()


if __name__ == "__main__":
    unittest.main()
