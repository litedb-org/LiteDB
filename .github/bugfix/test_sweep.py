"""Hosted sweeps resume precise journals, isolate blockers and preserve explicit pauses."""

import copy
from pathlib import Path
import subprocess
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, patch

from errors import InfrastructureError
from orchestrate import TEST_SOURCE
from state import Rejected
from sweep_plan import create, specification, validate
from sweep_runtime import advance, command, reconcile_deferred
from sweep_store import SweepStore


def fixture():
    args = SimpleNamespace(repo="owner/repo", sweep="batch", scheduler_sha="d" * 40, workflow_sha="c" * 40,
                           workflow_ref="automation/runtime", campaign_prefix="batch", issues=[1002, 2802],
                           max_runs=40, timeout_minutes=180, repository=Path("repo"))
    contracts = {str(issue): {"inventory_issue": issue, "frozen_test_revision": TEST_SOURCE,
                 "regressions": [{"name": f"LiteDB.Tests.Issue{issue}.Bad"}], "controls": [{"name": f"LiteDB.Tests.Issue{issue}.Good"}],
                 "allowed_production_paths": [f"LiteDB/{issue}.cs"]} for issue in args.issues}
    return args, contracts, create(args, contracts)


class SweepTests(unittest.TestCase):
    def setUp(self):
        self.args, self.contracts, self.manifest = fixture()
        self.journal = Mock(owner={"run_id": 11, "run_attempt": 1})
        self.persisted = copy.deepcopy(self.manifest)
        def save(value, expected):
            self.assertEqual(self.persisted, expected)
            self.persisted = copy.deepcopy(value)
            return copy.deepcopy(value)
        self.journal.save.side_effect = save
        self.integration = Mock()
        self.integration.read.return_value = None, "f" * 40
        self.decision = {"issue": 1002, "campaign": "batch-1002", "action": "start", "phase": "baseline",
                         "base_sha": "a" * 40, "candidate_sha": None, "resume_integration": False}
        self.state = {"campaign": "batch-1002", "phase": "baseline", "base_sha": "a" * 40, "candidate_sha": None,
                      "paused": False, "orchestration": {"requests": {}}, "history": []}

    def tick(self, decision=None, state=None, result=0, error=None):
        with patch("sweep_runtime.pinned_manifest"), patch("sweep_runtime.IntegrationStore", return_value=self.integration), \
                patch("sweep_runtime.inspect_queue_issue", return_value=decision or self.decision), \
                patch("sweep_runtime.campaign_state", return_value=state or self.state), \
                patch("sweep_runtime.git"), patch("sweep_runtime.worktree") as tree, \
                patch("sweep_runtime.command", return_value=result, side_effect=error) as child:
            tree.return_value.__enter__.return_value = Path("pinned")
            return advance(self.args, self.manifest, self.journal, self.contracts), child

    def test_one_tick_dispatches_only_pinned_orchestrator_and_keeps_campaign_active(self):
        result, child = self.tick()
        self.assertEqual("active", result["issues"]["1002"]["status"])
        self.assertEqual(1, child.call_count)
        self.assertEqual(Path("pinned/.github/bugfix/orchestrate.py"), child.call_args.args[0])
        self.assertIn("--tick", child.call_args.args[1])
        self.assertEqual("baseline", result["last_tick"]["campaign_phase"])

    def test_paused_campaign_stops_whole_sweep_without_dispatch(self):
        decision = {**self.decision, "action": "paused"}
        result, child = self.tick(decision, {**self.state, "paused": True})
        self.assertTrue(result["paused"])
        child.assert_not_called()
        self.tick()
        self.assertTrue(self.manifest["paused"])

    def test_blocked_issue_is_deferred_once_then_independent_issue_advances(self):
        decision = {**self.decision, "action": "defer_blocked", "phase": "blocked"}
        result, child = self.tick(decision, {**self.state, "phase": "blocked"})
        self.assertEqual("deferred", result["issues"]["1002"]["status"])
        child.assert_not_called()
        next_decision = {**self.decision, "issue": 2802, "campaign": "batch-2802", "base_sha": "b" * 40}
        result, child = self.tick(next_decision, {**self.state, "campaign": "batch-2802", "base_sha": "b" * 40})
        self.assertEqual("active", result["issues"]["2802"]["status"])
        self.assertEqual("b" * 40, result["issues"]["2802"]["decision"]["base_sha"])
        self.assertEqual(1, child.call_count)

    def test_shared_scope_deferred_dependency_is_not_dispatched(self):
        self.manifest["issues"]["1002"]["status"] = "deferred"
        self.contracts["2802"]["allowed_production_paths"] = self.contracts["1002"]["allowed_production_paths"]
        self.persisted = copy.deepcopy(self.manifest)
        result, child = self.tick({**self.decision, "issue": 2802, "campaign": "batch-2802"})
        self.assertEqual([1002], result["issues"]["2802"]["blocked_by"])
        child.assert_not_called()

    def test_ready_candidate_only_uses_verified_integrator_with_resume(self):
        decision = {**self.decision, "phase": "ready", "candidate_sha": "e" * 40, "resume_integration": True}
        self.integration.read.return_value = {"active": True, "campaign": "batch-1002"}, "f" * 40
        _, child = self.tick(decision, {**self.state, "phase": "ready", "candidate_sha": "e" * 40})
        self.assertEqual(Path("pinned/.github/bugfix/integrate.py"), child.call_args.args[0])
        self.assertIn("--apply", child.call_args.args[1])
        self.assertIn("--resume", child.call_args.args[1])

    def test_repeated_global_outage_retains_same_campaign_and_recovers_after_backoff(self):
        for _ in range(4):
            self.manifest["cooldown_until"] = 0
            self.persisted = copy.deepcopy(self.manifest)
            result, _ = self.tick(error=InfrastructureError("API unavailable"))
            self.assertEqual("active", result["issues"]["1002"]["status"])
            self.assertEqual(0, result["issues"]["1002"]["tick_errors"])
            self.assertGreater(result["cooldown_until"], 0)
        self.manifest["cooldown_until"] = 0
        self.persisted = copy.deepcopy(self.manifest)
        result, child = self.tick()
        self.assertEqual(1, child.call_count)
        self.assertEqual("active", result["issues"]["1002"]["status"])
        self.assertEqual(0, result["infrastructure_streak"])
        result, _ = self.tick(error=InfrastructureError("One later outage"))
        self.assertEqual(1, result["infrastructure_streak"])

    def test_semantic_integration_failure_defers_but_api_outage_retries_same_transaction(self):
        decision = {**self.decision, "phase": "ready", "candidate_sha": "e" * 40}
        for _ in range(3):
            self.manifest["cooldown_until"] = 0
            self.persisted = copy.deepcopy(self.manifest)
            result, _ = self.tick(decision, {**self.state, "phase": "ready"}, result=1)
        self.assertEqual("deferred", result["issues"]["1002"]["status"])
        self.setUp()
        for _ in range(4):
            self.manifest["cooldown_until"] = 0
            self.persisted = copy.deepcopy(self.manifest)
            result, _ = self.tick(decision, {**self.state, "phase": "ready"}, result=75)
        self.assertEqual("active", result["issues"]["1002"]["status"])
        self.assertEqual(0, result["issues"]["1002"]["tick_errors"])

    def test_external_verified_acceptance_releases_a_dependency_only_once(self):
        self.manifest["issues"]["1002"]["status"] = "deferred"
        dependent = self.manifest["issues"]["2802"]
        dependent.update(status="deferred", blocked_by=[1002])
        with patch("sweep_runtime.inspect_queue_issue", return_value={"action": "defer_blocked"}):
            reconcile_deferred(self.args, self.manifest, self.contracts)
        self.assertEqual("deferred", dependent["status"])
        self.manifest["recovery_cursor"] = 0
        with patch("sweep_runtime.inspect_queue_issue", return_value={"action": "skip_accepted"}):
            reconcile_deferred(self.args, self.manifest, self.contracts)
        self.assertEqual("pending", dependent["status"])
        self.assertEqual(1, dependent["dependency_revisits"])
        dependent["status"] = "deferred"
        with patch("sweep_runtime.inspect_queue_issue", return_value={"action": "defer_blocked"}):
            reconcile_deferred(self.args, self.manifest, self.contracts)
        self.assertEqual("deferred", dependent["status"])

    def test_host_timeout_is_infrastructure_not_correctness(self):
        with patch("sweep_runtime.subprocess.run", side_effect=subprocess.TimeoutExpired("tick", 360)):
            with self.assertRaises(InfrastructureError):
                command(Path("tick.py"), [], Path("repo"))

    def test_daily_credit_notice_globally_cools_without_spending_repair_attempt(self):
        state = {**self.state, "phase": "repairing", "orchestration": {"requests": {}, "cooldown_until": 10**12}}
        result, _ = self.tick(state=state)
        self.assertEqual(10**12, result["cooldown_until"])
        self.assertEqual(0, result["issues"]["1002"]["tick_errors"])
        _, child = self.tick(state=state)
        child.assert_not_called()

    def test_no_all_fixed_claim_when_any_issue_is_deferred(self):
        self.manifest["issues"]["1002"]["status"] = "deferred"
        self.manifest["issues"]["2802"]["status"] = "accepted"
        self.persisted = copy.deepcopy(self.manifest)
        result, child = self.tick()
        self.assertEqual("needs_recovery", result["phase"])
        child.assert_not_called()

    def test_specification_rejects_changed_runtime_scope_budget_and_contract(self):
        validate(self.manifest, "batch", self.contracts)
        for key, value in (("workflow_sha", "bad"), ("issues", [1002, 1002]), ("max_runs", 41),
                           ("timeout_minutes", 181), ("test_source_sha", "a" * 40), ("scheduler_sha", "bad")):
            changed = copy.deepcopy(self.manifest)
            changed["specification"][key] = value
            with self.subTest(key=key), self.assertRaises(Rejected):
                validate(changed, "batch", self.contracts)


class SweepLeaseTests(unittest.TestCase):
    def test_active_owner_cannot_be_stolen_and_dead_owner_can_be_reclaimed(self):
        store = Mock()
        old = {"active": True, "owner": {"run_id": 10, "run_attempt": 1}, "token": "old"}
        store.read.return_value = old, "a" * 40
        current = {"path": ".github/workflows/bugfix-sweep.yml", "status": "in_progress", "run_attempt": 1,
                   "event": "schedule", "head_branch": "dev", "head_sha": "b" * 40}
        for status in ("in_progress", "completed"):
            with patch("sweep_store.IntegrationStore", return_value=store), patch("sweep_store.github", side_effect=[
                    current, {"default_branch": "dev"}, {"type": "file", "sha": "c" * 40}, {"status": status, "run_attempt": 1}]):
                journal = SweepStore("owner/repo", "batch", 11, 1, "d" * 40)
                if status == "in_progress":
                    with self.assertRaisesRegex(Rejected, "live Actions"):
                        journal.acquire()
                    store.commit.assert_not_called()
                else:
                    journal.acquire()
                    self.assertEqual("c" * 40, journal.bootstrap_blob_sha)
                    store.commit.assert_called_once()

    def test_changed_manifest_fails_compare_and_swap_without_write(self):
        with patch("sweep_store.IntegrationStore"):
            journal = SweepStore("owner/repo", "batch", 11)
        journal.locked_snapshot = Mock(return_value=({}, {"other": True}, "a" * 40))
        with self.assertRaisesRegex(Rejected, "concurrently"):
            journal.save({"new": True}, {"old": True})
        journal.store.commit.assert_not_called()


if __name__ == "__main__":
    unittest.main()
