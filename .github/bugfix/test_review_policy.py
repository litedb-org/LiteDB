"""Nit-only approval and complete mixed-severity repair feedback."""
import unittest

from feedback import repair_feedback
from review_policy import only_nits, validate_review
from test_campaign import CampaignLoopTests, SimulatedRuns


def finding(severity):
    return {"severity": severity, "summary": "Observed finding", "path": "source.cs", "evidence": "Inspected diff"}


class ReviewPolicyTests(unittest.TestCase):
    def test_only_nits_can_pass_but_small_defects_cannot(self):
        for findings in ([], [finding("nit")]):
            validate_review("pass", findings)
        for severity in ("minor", "major", "critical", "unknown"):
            with self.assertRaises(ValueError):
                validate_review("pass", [finding("nit"), finding(severity)])
        self.assertFalse(only_nits([{"summary": "unclassified"}]))

    def test_nits_cannot_request_changes_and_missing_evidence_stays_inconclusive(self):
        with self.assertRaises(ValueError):
            validate_review("changes_requested", [finding("nit")])
        validate_review("inconclusive", [finding("minor")])
        validate_review("changes_requested", [finding("nit"), finding("minor")])

    def test_oversized_feedback_blocks_without_losing_findings(self):
        state = {"candidate_sha": "a", "history": [], "orchestration": {"review_reports": {"a": [
            {"role": "behavior", "run_id": 1, "findings": [dict(finding("major"), evidence="x" * 24000)]}
        ]}}}
        with self.assertRaisesRegex(ValueError, "must not be truncated"):
            repair_feedback(state)

    def test_ci_failure_before_rereview_keeps_prior_nit_obligations(self):
        import json
        prior = {"reviews": [{"role": "behavior", "run_id": 12,
                              "findings": [finding("nit"), finding("minor")]}]}
        state = {"candidate_sha": "b", "history": [], "orchestration": {"review_reports": {},
            "requests": {"fix-2": {"candidate_sha": "b", "inputs": {"feedback": json.dumps(prior)}}}}}
        self.assertEqual(prior["reviews"], json.loads(repair_feedback(state))["reviews"])
        state["orchestration"]["review_reports"]["b"] = [
            {"role": "behavior", "run_id": 13, "outcome": "pass", "findings": []}]
        self.assertEqual([], json.loads(repair_feedback(state))["reviews"])


class NitCampaignTests(CampaignLoopTests):
    def review(self, repo, state, workflow_run, artifacts, role):
        event = super().review(repo, state, workflow_run, artifacts, role)
        event["findings"] = [finding("nit")]
        if getattr(self, "mixed", False) and role == "compatibility" and state["repair_attempts"] == 1:
            event.update(outcome="fail", findings=[finding("nit"), finding("minor")])
        return event

    def test_nit_only_finishes_without_another_candidate(self):
        state = self.execute()
        self.assertEqual("ready", state["phase"])
        self.assertEqual(1, state["repair_attempts"])
        self.assertTrue(all(review["findings"] for review in state["reviews"].values()))

    def test_mixed_review_repairs_every_roles_findings(self):
        import json
        self.mixed = True
        state = self.execute()
        self.assertEqual("ready", state["phase"])
        self.assertEqual(2, state["repair_attempts"])
        fixes = [entry for entry in SimulatedRuns.log if entry[0] == "dispatch" and entry[1] == "bugfix-fix.lock.yml"]
        feedback = json.loads(fixes[-1][3]["feedback"])
        self.assertEqual({"behavior", "compatibility", "lifecycle"}, {review["role"] for review in feedback["reviews"]})
        self.assertEqual(4, sum(len(review["findings"]) for review in feedback["reviews"]))
        rereviews = [entry for entry in SimulatedRuns.log if entry[0] == "dispatch"
                     and entry[1] == "bugfix-validate.lock.yml"][-3:]
        self.assertTrue(all(json.loads(entry[3]["repair_requirements"]) == feedback for entry in rereviews))
