"""Integration must serialize writers and advance only the exact tested commit."""

import contextlib
import hashlib
import json
from pathlib import Path
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, patch

from integrate import add_controller_evidence, advance, execute, finish, tested_tree, validation_report
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
                  "coverage_gaps": [{"issue": 2854, "status": "unverified"}],
                  "source_context_changes": [{"test_name": "source-only guard", "behavior_unverified": True, "issue_credit": False}]}
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
        self.assertEqual(report["source_context_changes"], saved["issues"]["2874"]["source_context_changes"])
        self.assertNotIn("source-only guard", saved["issues"]["2874"]["tests"])
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

    def test_prepared_resume_uses_durable_raw_evidence_when_actions_are_unavailable(self):
        self.state["protocol"] = "compressed-v1"
        self.state["evidence"] = {"baseline": {"run_id": 111}, "broad": {"run_id": 222, "matrix": []}}
        args = SimpleNamespace(repo="owner/repo", campaign="canary", candidate_sha="d" * 40,
                               repository=Path("repo"), apply=True, resume=True)
        contract = {"frozen_test_revision": "b" * 40, "regressions": [], "controls": []}
        report = validation_report(self.state, "c" * 40, self.state["evidence"]["broad"], [])
        files = add_controller_evidence({"acceptance/run-111/raw.zip": b"raw"}, self.state,
                                        report, contract, {})
        lock = {"token": "lock-token", "phase": "prepared", "active": True,
                "candidate_tree_sha": "9" * 40, "evidence_prefix": "evidence/canary/",
                "evidence_manifest_sha256": hashlib.sha256(files["evidence-manifest.json"]).hexdigest()}
        store = Mock()
        store.read.return_value = (self.state, "1" * 40)
        store.acquire.return_value = lock
        store.require_lock.return_value = (lock, "2" * 40)
        store.read_prefix_at.return_value = files
        store.read_at.return_value = self.state
        unavailable = Mock(side_effect=Rejected("Actions artifact expired"))
        with patch("integrate.IntegrationStore", return_value=store), \
                patch("integrate.tested_tree", return_value="9" * 40), \
                patch("integrate.git", return_value=json.dumps({"issues": {"2874": contract}})), \
                patch("integrate.acceptance_evidence", unavailable), \
                patch("integrate.validate_archived_evidence") as archived, \
                patch("integrate.integration_head", return_value="d" * 40), \
                patch("integrate.advance"), patch("integrate.finish", return_value={**self.state, "phase": "integrated"}):
            self.assertEqual("integrated", execute(args)["phase"])
        unavailable.assert_not_called()
        archived.assert_called_once()
        store.read_prefix_at.assert_called_once_with("evidence/canary/", "2" * 40)

    def test_prepared_resume_rejects_changed_archive_before_branch_update(self):
        report = {"provenance": {"candidate_run_id": 222}, "target_jobs": [], "coverage_gaps": [],
                  "source_context_changes": []}
        contract = {"regressions": [], "controls": []}
        files = add_controller_evidence({"acceptance/raw.zip": b"raw"}, self.state, report, contract, {})
        digest = hashlib.sha256(files["evidence-manifest.json"]).hexdigest()
        files["acceptance/raw.zip"] = b"changed"
        store = Mock()
        store.read_prefix_at.return_value = files
        lock = {"evidence_prefix": "evidence/canary/", "evidence_manifest_sha256": digest}
        with self.assertRaisesRegex(Rejected, "durable manifest"):
            from integrate import prepared_evidence
            prepared_evidence(store, self.state, lock, "2" * 40, report, contract, {})


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

    def test_prepared_archive_reader_walks_only_the_bound_prefix(self):
        trees = {
            "root": {"tree": [{"path": "evidence", "type": "tree", "sha": "evidence"}]},
            "evidence": {"tree": [{"path": "canary", "type": "tree", "sha": "canary"}]},
            "canary": {"tree": [{"path": "report.json", "type": "blob", "mode": "100644",
                                  "sha": "blob", "size": 3},
                                 {"path": "nested", "type": "tree", "sha": "nested"}]},
            "nested": {"tree": [{"path": "raw.zip", "type": "blob", "mode": "100644",
                                  "sha": "archive", "size": 4}]},
        }

        def api(repo, path):
            kind, sha = path.split("/")[-2:]
            if kind == "trees":
                return trees[sha]
            return {"encoding": "base64", "content": "cmF3" if sha == "blob" else "emlwIQ=="}

        with patch("integrate_storage.github", side_effect=api):
            files = self.store.read_prefix_at("evidence/canary/", "root")
        self.assertEqual({"report.json": b"raw", "nested/raw.zip": b"zip!"}, files)


if __name__ == "__main__":
    unittest.main()
