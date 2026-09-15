"""Acceptance invariants; run: python -m unittest discover -s .github/bugfix."""

import copy
import unittest
from unittest.mock import patch

from state import IDENTITY, ROLES, Rejected, apply_event, new_state
from storage import Store, verify_run


class CampaignTests(unittest.TestCase):
    def setUp(self):
        self.state = new_state("issue-2874", 2874, "a" * 40, "b" * 40, "c" * 40)
        self.sequence = 0

    def event(self, kind, outcome=None, **overrides):
        self.sequence += 1
        event = {key: self.state[key] for key in IDENTITY}
        event.update(schema_version=1, event_id=f"event-{self.sequence}", kind=kind,
                     candidate_sha=self.state["candidate_sha"], run_id=self.sequence,
                     environment="ubuntu-24.04/net8.0/x64", artifact="bugfix-evidence")
        if outcome:
            event["outcome"] = outcome
        event.update(overrides)
        return event

    def record(self, kind, outcome=None, **overrides):
        self.state = apply_event(self.state, self.event(kind, outcome, **overrides))

    def candidate(self):
        self.record("baseline", "bug_present")
        self.record("candidate", candidate_sha="d" * 40)

    def reviewed(self):
        self.candidate()
        self.record("focused", "behavior_correct")
        self.record("broad", "pass")
        for role in ROLES:
            self.record("review", "pass", role=role, findings=[])

    def test_happy_path_requires_every_gate_then_integrates_exact_commit(self):
        self.reviewed()
        self.assertEqual("acceptance", self.state["phase"])
        self.record("acceptance", "pass")
        self.record("integrated", expected_base_sha="a" * 40, integration_sha="d" * 40)
        self.assertEqual("integrated", self.state["phase"])

    def test_candidate_without_confirmed_baseline_rejected(self):
        with self.assertRaises(Rejected):
            self.record("candidate", candidate_sha="d" * 40)

    def test_already_passing_baseline_does_not_authorize_fix(self):
        self.record("baseline", "behavior_correct")
        self.assertEqual("blocked", self.state["phase"])

    def test_missing_or_stale_identity_never_mutates_state(self):
        self.candidate()
        original = copy.deepcopy(self.state)
        for field in (*IDENTITY, "candidate_sha"):
            with self.subTest(field=field):
                event = self.event("focused", "behavior_correct")
                del event[field]
                with self.assertRaises(Rejected):
                    apply_event(self.state, event)
                event[field] = "stale"
                with self.assertRaises(Rejected):
                    apply_event(self.state, event)
        self.assertEqual(original, self.state)

    def test_duplicate_completion_is_idempotent(self):
        event = self.event("baseline", "bug_present")
        self.state = apply_event(self.state, event)
        self.assertEqual(self.state, apply_event(self.state, event))
        event["outcome"] = "behavior_correct"
        with self.assertRaises(Rejected):
            apply_event(self.state, event)

    def test_missing_evidence_rejected(self):
        for field in ("run_id", "environment", "artifact"):
            with self.subTest(field=field):
                event = self.event("baseline", "bug_present")
                del event[field]
                with self.assertRaises(Rejected):
                    apply_event(self.state, event)

    def test_three_failed_repairs_exhaust_budget(self):
        self.record("baseline", "bug_present")
        for character in ("d", "e", "f"):
            self.record("candidate", candidate_sha=character * 40)
            self.record("focused", "bug_present")
        self.assertEqual("blocked", self.state["phase"])
        self.assertEqual(3, self.state["repair_attempts"])
        with self.assertRaises(Rejected):
            self.record("candidate", candidate_sha="1" * 40)

    def test_infrastructure_retries_do_not_consume_repair_budget(self):
        self.candidate()
        for _ in range(2):
            self.record("focused", "harness_error")
            self.assertEqual("focused", self.state["phase"])
        self.assertEqual(1, self.state["repair_attempts"])
        self.record("focused", "harness_error")
        self.assertEqual("blocked", self.state["phase"])

    def test_missing_third_review_blocks_acceptance(self):
        self.candidate()
        self.record("focused", "behavior_correct")
        self.record("broad", "pass")
        for role in ROLES[:2]:
            self.record("review", "pass", role=role, findings=[])
        with self.assertRaises(Rejected):
            self.record("acceptance", "pass")

    def test_review_findings_and_reused_runs_cannot_approve(self):
        self.candidate()
        self.record("focused", "behavior_correct")
        self.record("broad", "pass")
        with self.assertRaises(Rejected):
            self.record("review", "pass", role="behavior", findings=["new defect"])
        self.record("review", "pass", role="behavior", findings=[], run_id=100)
        with self.assertRaises(Rejected):
            self.record("review", "pass", role="compatibility", findings=[], run_id=100)

    def test_failed_review_invalidates_approvals_and_returns_to_repair(self):
        self.candidate()
        self.record("focused", "behavior_correct")
        self.record("broad", "pass")
        self.record("review", "pass", role="behavior", findings=[])
        self.record("review", "fail", role="compatibility", findings=["old files fail to reopen"])
        self.assertEqual("repairing", self.state["phase"])
        self.assertEqual({}, self.state["reviews"])
        old_sha = self.state["candidate_sha"]
        self.record("candidate", candidate_sha="e" * 40)
        with self.assertRaises(Rejected):
            self.record("focused", "behavior_correct", candidate_sha=old_sha)

    def test_moved_base_and_untested_merge_commit_rejected(self):
        self.reviewed()
        self.record("acceptance", "pass")
        with self.assertRaises(Rejected):
            self.record("integrated", expected_base_sha="e" * 40, integration_sha="d" * 40)
        with self.assertRaises(Rejected):
            self.record("integrated", expected_base_sha="a" * 40, integration_sha="e" * 40)

    def test_pause_requires_explicit_resume(self):
        self.record("pause")
        with self.assertRaises(Rejected):
            self.record("baseline", "bug_present")
        self.record("resume")
        self.record("baseline", "bug_present")
        self.assertEqual("repairing", self.state["phase"])

    def test_unknown_schema_and_inconclusive_evidence_fail_closed(self):
        with self.assertRaises(Rejected):
            self.record("baseline", "bug_present", schema_version=2)
        self.record("baseline", "inconclusive")
        self.assertEqual("blocked", self.state["phase"])


class ProvenanceTests(unittest.TestCase):
    def setUp(self):
        self.event = {"run_id": 15, "workflow_sha": "c" * 40,
                      "kind": "broad", "outcome": "pass", "artifact": "bugfix-evidence"}
        self.workflow = ".github/workflows/bugfix-check.yml"
        self.run = {"head_sha": "c" * 40, "path": self.workflow, "event": "workflow_dispatch",
                    "status": "completed", "conclusion": "success"}
        self.artifacts = {"total_count": 1, "artifacts": [{"name": "bugfix-evidence", "expired": False}]}

    def verify(self):
        with patch("storage.github", side_effect=[self.run, self.artifacts]):
            verify_run("owner/repo", self.event, [self.workflow])

    def test_valid_run_and_artifact(self):
        with patch("storage.download", return_value=b"archive") as download, \
                patch("storage.validate", return_value={"report_sha256": "digest"}) as validate:
            self.verify()
        download.assert_called_once()
        validate.assert_called_once_with(b"archive", self.event)

    def test_stale_untrusted_or_failed_run_rejected(self):
        for field, value in (("head_sha", "a" * 40), ("path", "untrusted.yml"),
                             ("event", "pull_request"), ("status", "in_progress"),
                             ("conclusion", "failure")):
            with self.subTest(field=field):
                original = self.run[field]
                self.run[field] = value
                with self.assertRaises(Rejected):
                    self.verify()
                self.run[field] = original

    def test_expired_and_missing_artifacts_rejected(self):
        self.artifacts["artifacts"][0]["expired"] = True
        with self.assertRaises(Rejected):
            self.verify()
        self.artifacts["artifacts"] = []
        with self.assertRaises(Rejected):
            self.verify()

    def test_reproduced_candidate_bug_is_failure_evidence_not_positive(self):
        self.event.update(kind="focused", outcome="bug_present")
        self.run["conclusion"] = "failure"
        with patch("storage.download") as download:
            self.verify()
        download.assert_not_called()

    def test_state_push_uses_exact_lease_and_does_not_retry_conflict(self):
        state = new_state("canary", 2874, "a" * 40, "b" * 40, "c" * 40)
        calls = []

        def execute(command, **kwargs):
            calls.append(command)
            if "push" in command:
                raise Rejected("stale lease")
            return "canary.json" if "diff" in command else ""

        with patch("storage.run", side_effect=execute):
            with self.assertRaisesRegex(Rejected, "stale lease"):
                Store("owner/repo").write(state, "f" * 40)
        pushes = [call for call in calls if "push" in call]
        self.assertEqual(1, len(pushes))
        self.assertIn("--force-with-lease=refs/heads/automation/bugfix-state:" + "f" * 40, pushes[0])


if __name__ == "__main__":
    unittest.main()
