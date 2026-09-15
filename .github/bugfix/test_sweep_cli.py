"""Bootstrap/controller pins and control operations are authenticated before writes."""

import copy
import json
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, patch

from errors import InfrastructureError
from state import Rejected
from storage import github, Store
from sweep import arguments, execute
from test_sweep import fixture


class SweepCliTests(unittest.TestCase):
    def setUp(self):
        self.args, self.contracts, self.manifest = fixture()
        self.args.operation = "tick"
        self.args.owner_run_id = 11
        self.args.owner_run_attempt = 1
        self.args.dry_run = False
        self.args.reason = "Explicit reviewed control"
        self.manifest["bootstrap_blob_sha"] = "e" * 40
        self.store = Mock(bootstrap_blob_sha="e" * 40, owner={"run_id": 11, "run_attempt": 1})
        self.store.read.return_value = copy.deepcopy(self.manifest), "f" * 40
        self.store.locked_snapshot.return_value = ({}, copy.deepcopy(self.manifest), "f" * 40)

    def call(self):
        with patch("sweep.SweepStore", return_value=self.store), patch("sweep.git", return_value=self.args.scheduler_sha), \
                patch("sweep.pinned_manifest", return_value={"issues": self.contracts}), patch("sweep.advance") as advance:
            result = execute(self.args)
            return result, advance

    def test_changed_bootstrap_cannot_mutate_existing_sweep(self):
        self.store.bootstrap_blob_sha = "b" * 40
        with self.assertRaisesRegex(Rejected, "Bootstrap workflow changed"):
            self.call()
        self.store.save.assert_not_called()
        self.store.release.assert_called_once()

    def test_changed_scheduler_cannot_acquire_lease(self):
        self.args.scheduler_sha = "b" * 40
        with self.assertRaisesRegex(Rejected, "Scheduler pin differs"):
            self.call()
        self.store.acquire.assert_not_called()

    def test_init_is_idempotent_and_different_spec_rejected(self):
        self.args.operation = "init"
        result, _ = self.call()
        self.assertEqual(self.manifest, result)
        self.store.save.assert_not_called()
        self.args.issues = [1002]
        with self.assertRaisesRegex(Rejected, "scope differs"):
            self.call()

    def test_pause_and_resume_are_durable_control_events(self):
        self.args.operation = "pause"
        result, advance = self.call()
        self.assertTrue(result["paused"])
        self.assertEqual("pause", result["history"][-1]["action"])
        advance.assert_not_called()
        self.store.read.return_value = result, "f" * 40
        self.store.locked_snapshot.return_value = ({}, result, "f" * 40)
        self.args.operation = "resume"
        resumed, _ = self.call()
        self.assertFalse(resumed["paused"])
        self.assertEqual(self.args.reason, resumed["history"][-1]["reason"])

    def test_control_requires_explicit_reason_and_dry_run_never_takes_lease(self):
        with self.assertRaisesRegex(Rejected, "reason"):
            arguments(["resume", "--repo", "owner/repo", "--sweep", "batch"])
        self.args.dry_run = True
        result, advance = self.call()
        self.assertTrue(result["dry_run"])
        self.store.acquire.assert_not_called()
        self.store.save.assert_not_called()
        advance.assert_not_called()

    def test_manifest_race_after_lease_never_advances(self):
        self.store.locked_snapshot.return_value = ({}, {"changed": True}, "a" * 40)
        with self.assertRaisesRegex(Rejected, "changed before"):
            self.call()
        self.store.save.assert_not_called()

    def test_api_json_transport_failures_are_infrastructure(self):
        for raw in ("", "{", "null", "123"):
            with self.subTest(raw=raw), patch("storage.run", return_value=raw), self.assertRaises(InfrastructureError):
                github("owner/repo", "actions/runs/123")
        with patch("storage.subprocess.run", return_value=SimpleNamespace(returncode=0, stdout="{", stderr="")):
            with self.assertRaises(InfrastructureError):
                Store("owner/repo").current_sha()


if __name__ == "__main__":
    unittest.main()
