"""A new grading definition preserves the exact candidate and original reviews."""

import copy
import unittest
from pathlib import Path
from unittest.mock import patch

from evidence import event_for
from state import ROLES, Rejected, apply_event, new_state
from test_profiles import profile_fixture
from revalidation_proof import verify_prior


class RevalidationStateTests(unittest.TestCase):
    def setUp(self):
        self.state = new_state("canary", 2874, "a" * 40, "b" * 40, "c" * 40)
        self.state.update(candidate_sha="d" * 40, phase="acceptance", paused=True, repair_attempts=1)
        self.state["reviews"] = {role: event_for(self.state, "review", 10 + index, role=role, outcome="pass", findings=[])
                                  for index, role in enumerate(ROLES)}
        self.state["evidence"] = {kind: {"run_id": index} for index, kind in enumerate(("baseline", "focused", "broad", "acceptance"), 1)}
        lanes = [{"artifact_sha256": "1" * 64, "classification_changes": []} for _ in range(6)]
        lanes[0]["classification_changes"] = [{"name": "ExistingFailure"}]
        self.event = event_for(self.state, "revalidate", 4, reason="Reviewed only JIT presentation normalization",
                               prior_acceptance_run_id=4, classification_evidence=lanes,
                               check_definition={"workflow_sha": "e" * 40, "workflow_ref": "automation/check-v5",
                                                 "normalization_sha256": "f" * 64},
                               review_run_ids={role: self.state["reviews"][role]["run_id"] for role in ROLES},
                               definition_diff=[{"path": "scripts/bugfix/failure-normalization.json"}],
                               acceptance_profile=profile_fixture())

    def test_preserves_original_evidence_and_reviews_with_separate_check_identity(self):
        result = apply_event(self.state, self.event)
        for key in ("candidate_sha", "workflow_sha", "reviews", "evidence", "repair_attempts"):
            self.assertEqual(self.state[key], result[key])
        self.assertFalse(result["paused"])
        self.assertEqual("e" * 40, result["check_definition"]["workflow_sha"])
        self.assertEqual([self.event], result["history"])

    def test_missing_authorization_or_changed_candidate_review_rejected(self):
        for key, value in (("reason", ""), ("candidate_sha", "f" * 40),
                           ("review_run_ids", {}), ("classification_evidence", []), ("acceptance_profile", None)):
            with self.subTest(key=key), self.assertRaises(Rejected):
                apply_event(self.state, {**self.event, key: value})
        changed = copy.deepcopy(self.state)
        changed["reviews"].pop("compatibility")
        with self.assertRaises(Rejected):
            apply_event(changed, self.event)

    def test_cancelled_stale_check_definition_cannot_advance_revalidation(self):
        state = apply_event(self.state, self.event)
        stale = event_for(state, "acceptance", 99, check_workflow_sha="c" * 40, outcome="pass")
        # event_for derives the current definition; simulate a stale external event explicitly.
        stale["check_workflow_sha"] = "c" * 40
        with self.assertRaisesRegex(Rejected, "Stale check definition"):
            apply_event(state, stale)


class PriorRunAuthenticationTests(unittest.TestCase):
    def setUp(self):
        self.state = new_state("canary", 2874, "a" * 40, "b" * 40, "c" * 40)
        self.state["candidate_sha"] = "d" * 40
        self.state["passing_contract"] = {"state_commit": "e" * 40, "ledger_sha256": "f" * 64}
        self.inputs = {"level": "acceptance", "issue": "2874", "base_sha": "a" * 40, "candidate_sha": "d" * 40,
                       "accepted_state_sha": "e" * 40, "accepted_ledger_sha256": "f" * 64}
        self.state["orchestration"] = {"requests": {"acceptance-1-0": {
            "run_id": 4, "request_id": "bugfix-unique-request", "inputs": self.inputs}}}
        self.run = {"id": 4, "head_sha": "c" * 40, "path": ".github/workflows/bugfix-check.yml",
                    "event": "workflow_dispatch", "status": "completed", "conclusion": "failure",
                    "display_title": "Bugfix bugfix-unique-request 2874 acceptance"}

    def verify(self, jobs=None):
        with patch("revalidation_proof.github", side_effect=[self.run, jobs]), \
                patch("revalidation_proof.download") as download:
            try:
                return verify_prior("owner/repo", Path.cwd(), self.state, 4, Path.cwd())
            finally:
                download.assert_not_called()

    def test_wrong_definition_cancelled_run_or_request_token_rejected_before_download(self):
        for key, value in (("head_sha", "0" * 40), ("conclusion", "cancelled"),
                           ("display_title", "prefix-bugfix-unique-request-suffix")):
            original = self.run[key]
            self.run[key] = value
            with self.subTest(key=key), self.assertRaises(Rejected):
                self.verify()
            self.run[key] = original

    def test_changed_candidate_dispatch_or_incomplete_matrix_rejected(self):
        self.inputs["candidate_sha"] = "0" * 40
        with self.assertRaisesRegex(Rejected, "candidate_sha"):
            self.verify()
        self.inputs["candidate_sha"] = "d" * 40
        with self.assertRaisesRegex(Rejected, "missing, cancelled or incomplete"):
            self.verify({"total_count": 0, "jobs": []})


if __name__ == "__main__":
    unittest.main()
