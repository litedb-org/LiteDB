"""Integration must serialize writers and advance only the exact tested commit."""

import contextlib
import json
from pathlib import Path
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, patch

from integrate import advance, execute, finish, tested_tree
from integrate_storage import IntegrationStore
from state import Rejected, new_state


class IntegrationTests(unittest.TestCase):
    def setUp(self):
        self.state = new_state("canary", 2874, "a" * 40, "b" * 40, "c" * 40)
        self.state.update(phase="ready", candidate_sha="d" * 40)
        self.state["evidence"] = {"baseline": {"run_id": 111}, "acceptance": {"run_id": 222, "matrix": []}}
        self.lock = {"token": "lock-token", "phase": "prepared", "active": True}

    def test_branch_update_uses_exact_expected_base_lease(self):
        with patch("integrate.integration_head", side_effect=["a" * 40, "d" * 40]), patch("integrate.git") as git:
            advance(Path("repo"), "owner/repo", self.state, self.lock)
        self.assertIn("--force-with-lease=refs/heads/integration/bugfixes:" + "a" * 40, git.call_args.args)
        self.assertIn("d" * 40 + ":refs/heads/integration/bugfixes", git.call_args.args)

    def test_moved_base_rejects_without_a_push(self):
        with patch("integrate.integration_head", return_value="e" * 40), patch("integrate.git") as git:
            with self.assertRaisesRegex(Rejected, "moved"):
                advance(Path("repo"), "owner/repo", self.state, self.lock)
        git.assert_not_called()

    def test_uncertain_success_can_resume_only_with_prepared_intent(self):
        with patch("integrate.integration_head", return_value="d" * 40), patch("integrate.git") as git:
            advance(Path("repo"), "owner/repo", self.state, self.lock)
            with self.assertRaisesRegex(Rejected, "prepared intent"):
                advance(Path("repo"), "owner/repo", self.state, {"phase": "acquired"})
        git.assert_not_called()

    def test_remote_tree_mismatch_rejects(self):
        with patch("integrate.git", side_effect=["a" * 40, "d" * 40, "a" * 40, "e" * 40]), \
                patch("integrate.github", return_value={"tree": {"sha": "f" * 40}}):
            with self.assertRaisesRegex(Rejected, "tree differs"):
                tested_tree(Path("repo"), "owner/repo", self.state)

    def test_missing_ready_state_rejects_before_loading_remote_evidence(self):
        self.state["phase"] = "reviewing"
        args = SimpleNamespace(repo="owner/repo", campaign="canary", candidate_sha="d" * 40)
        store = Mock()
        store.read.return_value = (self.state, "1" * 40)
        with patch("integrate.IntegrationStore", return_value=store), patch("integrate.acceptance_evidence") as evidence:
            with self.assertRaisesRegex(Rejected, "ready"):
                execute(args)
        evidence.assert_not_called()

    def test_finish_preserves_existing_pass_ledger_and_explicit_quarantine(self):
        report = {"provenance": {"candidate_run_id": 123}, "target_jobs": ["test job"],
                  "coverage_gaps": [{"issue": 2854, "status": "unverified"}]}
        contract = {"regressions": [{"name": "new regression"}], "controls": [{"name": "valid input"}]}
        ledger = {"schema_version": 1, "issues": {"1000": {"tests": ["previous regression"]}}}
        store = Mock()
        store.require_lock.return_value = (self.lock, "1" * 40)
        store.read_at.side_effect = [self.state, ledger]
        result = finish(store, self.state, self.lock, report, contract, "e" * 40)
        self.assertEqual("integrated", result["phase"])
        files = store.commit.call_args.args[1]
        saved = json.loads(files["accepted-tests.json"])
        self.assertEqual(["previous regression"], saved["issues"]["1000"]["tests"])
        self.assertEqual(report["coverage_gaps"], saved["issues"]["2874"]["coverage_gaps"])
        self.assertNotIn("2854", saved["issues"])
        self.assertFalse(json.loads(files["integration-lock.json"])["active"])

    def invoke_pipeline(self, matrix_failure=False):
        args = SimpleNamespace(repo="owner/repo", campaign="canary", candidate_sha="d" * 40,
                               baseline_run=111, candidate_run=222, evidence_definition_sha="e" * 40,
                               grading_policy_sha="f" * 40, repository=Path("repo"), apply=True, resume=False)
        store = Mock()
        store.read.return_value = (self.state, "1" * 40)
        lock = {"token": "lock-token", "phase": "acquired", "active": True}
        store.acquire.return_value = lock
        store.require_lock.return_value = (lock, "1" * 40)
        store.read_at.return_value = self.state
        order = []
        store.commit.side_effect = lambda *args: order.append("durable-evidence")
        contract = {"frozen_test_revision": "b" * 40, "regressions": [], "controls": []}
        report = {"provenance": {}, "coverage_gaps": [{"issue": 2854, "status": "unverified"}]}
        evidence = Mock(return_value={"acceptance.zip": b"evidence"})
        if matrix_failure:
            evidence.side_effect = Rejected("Acceptance contains harness_error")
        with patch("integrate.IntegrationStore", return_value=store), patch("integrate.tested_tree", return_value="9" * 40), \
                patch("integrate.git", return_value=json.dumps({"issues": {"2874": contract}})), \
                patch("integrate.worktree", return_value=contextlib.nullcontext(Path("policy"))) as checkout, \
                patch("integrate.acceptance_evidence", evidence), patch("integrate.integration_head", return_value="a" * 40), \
                patch("integrate.advance", side_effect=lambda *args: order.append("branch-update")), \
                patch("integrate.finish", side_effect=lambda *args: order.append("accepted-ledger") or
                      {**self.state, "phase": "integrated"}):
            if matrix_failure:
                with self.assertRaisesRegex(Rejected, "harness_error"):
                    execute(args)
            else:
                self.assertEqual("integrated", execute(args)["phase"])
            checkout.assert_not_called()
        return store, order

    def test_evidence_is_durable_before_exact_branch_update(self):
        store, order = self.invoke_pipeline()
        self.assertEqual(["durable-evidence", "branch-update", "accepted-ledger"], order)
        files = store.commit.call_args.args[1]
        self.assertIn("evidence/canary/acceptance.zip", files)
        self.assertIn("evidence/canary/evidence-manifest.json", files)

    def test_bad_required_acceptance_prevents_lock_and_any_branch_update(self):
        store, order = self.invoke_pipeline(matrix_failure=True)
        self.assertEqual([], order)
        store.acquire.assert_not_called()
        store.commit.assert_not_called()


class IntegrationLockTests(unittest.TestCase):
    def setUp(self):
        self.identity = {"campaign": "canary", "base_sha": "a" * 40, "candidate_sha": "d" * 40}
        self.store = IntegrationStore("owner/repo")

    def test_new_lock_is_persisted_before_use(self):
        with patch.object(self.store, "read", return_value=(None, "f" * 40)), patch.object(self.store, "commit") as commit:
            lock = self.store.acquire(self.identity)
        self.assertTrue(lock["active"])
        self.assertEqual("f" * 40, commit.call_args.args[0])
        self.assertIn("integration-lock.json", commit.call_args.args[1])

    def test_other_writer_and_implicit_resume_rejected(self):
        lock = {"active": True, "campaign": "canary", "identity": self.identity}
        with patch.object(self.store, "read", return_value=(lock, "f" * 40)), patch.object(self.store, "commit") as commit:
            with self.assertRaisesRegex(Rejected, "explicit --resume"):
                self.store.acquire(self.identity)
            with self.assertRaisesRegex(Rejected, "Another integration"):
                self.store.acquire({**self.identity, "candidate_sha": "e" * 40}, resume=True)
            self.assertEqual(lock, self.store.acquire(self.identity, resume=True))
        commit.assert_not_called()

    def test_data_commit_uses_exact_lease_without_retrying_conflict(self):
        calls = []

        def command(arguments, **kwargs):
            calls.append(arguments)
            if "push" in arguments:
                raise Rejected("stale lease")
            return ""

        with patch("integrate_storage.run", side_effect=command):
            with self.assertRaisesRegex(Rejected, "stale lease"):
                self.store.commit("f" * 40, {"integration-lock.json": b"{}"}, "Record evidence")
        pushes = [call for call in calls if "push" in call]
        self.assertEqual(1, len(pushes))
        self.assertIn("--force-with-lease=refs/heads/automation/bugfix-state:" + "f" * 40, pushes[0])


if __name__ == "__main__":
    unittest.main()
