"""Controller-owned digest and duplicate replay behavior at the CLI boundary."""

import contextlib
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import controller
from state import IDENTITY, Rejected, apply_event, new_state


class CliTests(unittest.TestCase):
    def setUp(self):
        self.state = new_state("canary", 2874, "a" * 40, "b" * 40, "c" * 40)
        self.event = {key: self.state[key] for key in IDENTITY}
        self.event.update(schema_version=1, event_id="baseline-1", kind="baseline",
                          candidate_sha=None, run_id=123, environment="linux-x64-net8.0",
                          artifact="checks", outcome="bug_present")
        self.hashes = {"report_sha256": "d" * 64, "artifact_sha256": "e" * 64}

    def invoke(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "event.json"
            path.write_text(json.dumps(self.event), encoding="utf-8")
            arguments = ["controller.py", "--repo", "owner/repo", "--campaign", "canary",
                         "record", "--event-json", str(path), "--allow-workflow", "checks.yml"]
            with patch("sys.argv", arguments), patch("controller.Store") as store, \
                    patch("controller.verify_run", return_value=self.hashes) as verify:
                store.return_value.read.return_value = (self.state, "f" * 40)
                store.return_value.write.return_value = "1" * 40
                with contextlib.redirect_stdout(io.StringIO()):
                    controller.main()
                return store.return_value, verify

    def test_verified_digests_are_persisted_with_evidence(self):
        store, verify = self.invoke()
        verify.assert_called_once()
        written = store.write.call_args.args[0]
        for field, value in self.hashes.items():
            self.assertEqual(value, written["history"][0][field])
            self.assertEqual(value, written["evidence"]["baseline"][field])

    def test_caller_cannot_supply_report_digest(self):
        self.event["report_sha256"] = "forged"
        with self.assertRaisesRegex(Rejected, "controller-owned"):
            self.invoke()

    def test_duplicate_event_does_not_require_retained_artifact(self):
        self.state = apply_event(self.state, {**self.event, **self.hashes})
        store, verify = self.invoke()
        verify.assert_not_called()
        store.write.assert_not_called()

    def test_changed_duplicate_does_not_reuse_verified_hash(self):
        self.state = apply_event(self.state, {**self.event, **self.hashes})
        self.event["artifact"] = "different-artifact"
        with self.assertRaisesRegex(Rejected, "reused"):
            self.invoke()


if __name__ == "__main__":
    unittest.main()
