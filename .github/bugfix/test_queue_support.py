"""Queue resume and accepted skips require matching immutable controller evidence."""

import copy
import base64
import json
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, patch

from orchestrate import TEST_SOURCE
from queue_support import inspect_queue_issue, pinned_manifest
from state import Rejected


class QueueDecisionTests(unittest.TestCase):
    def setUp(self):
        self.args = SimpleNamespace(repo="owner/repo", campaign_prefix="batch", workflow_sha="c" * 40,
                                    workflow_ref="automation/runtime", test_source_sha=TEST_SOURCE)
        self.head = "a" * 40
        self.ledger = {"schema_version": 1, "issues": {}}
        self.state = None
        self.lock = None
        self.accepted = None
        self.store = Mock()
        self.contracts = {"2874": {"regressions": [{"name": "LiteDB.Tests.Regression(Ã‚Â·Ã‚Â·Ã‚Â·)"}],
                                   "controls": [{"name": "LiteDB.Tests.Control"}]}}

    def inspect(self, is_ancestor=True):
        self.store.read.return_value = self.ledger, "f" * 40
        def read(name, sha):
            self.assertEqual("f" * 40, sha)
            return {"batch-2874": self.state, "integration-lock": self.lock, "old-2874": self.accepted}[name]
        self.store.read_at.side_effect = read
        with patch("queue_support.IntegrationStore", return_value=self.store), \
                patch("queue_support.github", side_effect=lambda repo, path: {"type": "file", "encoding": "base64", "size": len(json.dumps(getattr(self, "archived_contract", self.contracts["2874"])).encode()),
                      "content": base64.b64encode(json.dumps(getattr(self, "archived_contract", self.contracts["2874"])).encode()).decode()}
                      if path.startswith("contents/evidence/") else {"object": {"sha": self.head}}), \
                patch("queue_support.ancestor", return_value=is_ancestor):
            return inspect_queue_issue(self.args, 2874, self.contracts)

    def active(self, phase="broad"):
        self.state = {"campaign": "batch-2874", "issue": 2874, "workflow_sha": "c" * 40,
                      "test_source_sha": TEST_SOURCE, "orchestration": {"workflow_ref": "automation/runtime"},
                      "base_sha": "a" * 40, "candidate_sha": "d" * 40, "phase": phase,
                      "paused": False, "protocol": "compressed-v1"}

    def accepted_issue(self):
        self.archived_contract = copy.deepcopy(self.contracts["2874"])
        entry = {"campaign": "old-2874", "candidate_sha": "d" * 40, "test_source_sha": TEST_SOURCE,
                 "tests": ["LiteDB.Tests.Control", "LiteDB.Tests.Regression(Ã‚Â·Ã‚Â·Ã‚Â·)"]}
        self.ledger["issues"]["2874"] = entry
        self.accepted = {"phase": "integrated", "paused": False, "issue": 2874,
                         "candidate_sha": "d" * 40, "test_source_sha": TEST_SOURCE, "accepted_tests": copy.deepcopy(entry)}

    def test_fresh_base_and_existing_resume_identity_are_exact(self):
        self.assertEqual("start", self.inspect()["action"])
        self.active()
        resumed = self.inspect()
        self.assertEqual(("resume", "batch-2874", self.head), (resumed["action"], resumed["campaign"], resumed["base_sha"]))
        self.head = "e" * 40
        with self.assertRaisesRegex(Rejected, "base moved"):
            self.inspect()
        self.head = "a" * 40
        self.state["workflow_sha"] = "e" * 40
        with self.assertRaisesRegex(Rejected, "workflow_sha"):
            self.inspect()

    def test_paused_blocked_unknown_or_failed_campaign_never_resumes(self):
        for phase in ("blocked", "inconclusive", "failed", "unknown"):
            self.active(phase)
            with self.subTest(phase=phase), self.assertRaises(Rejected):
                self.inspect()
        self.active()
        self.state["paused"] = True
        with self.assertRaisesRegex(Rejected, "paused=True"):
            self.inspect()

    def test_only_ancestor_with_matching_integrated_contract_can_be_skipped(self):
        self.accepted_issue()
        self.assertEqual("skip_accepted", self.inspect()["action"])
        with self.assertRaisesRegex(Rejected, "not an ancestor"):
            self.inspect(is_ancestor=False)
        self.accepted["phase"] = "ready"
        with self.assertRaisesRegex(Rejected, "completed integrated"):
            self.inspect()
        self.accepted["phase"] = "integrated"
        self.ledger["issues"]["2874"]["tests"][1] = "LiteDB.Tests.Regression(Ãƒâ€šÃ‚Â·Ãƒâ€šÃ‚Â·Ãƒâ€šÃ‚Â·)"
        with self.assertRaisesRegex(Rejected, "test identities"):
            self.inspect()

    def test_same_names_with_changed_semantic_contract_cannot_skip(self):
        for field, value in (("review_requirements", {"behavior": ["New required proof"]}),
                             ("required_environments", ["windows-x64-net10.0"])):
            self.accepted_issue()
            self.contracts["2874"][field] = value
            with self.subTest(field=field), self.assertRaisesRegex(Rejected, "Accepted contract differs"):
                self.inspect()
            del self.contracts["2874"][field]

    def test_prepared_integration_resumes_only_same_candidate_and_owner(self):
        self.active("ready")
        self.head = self.state["candidate_sha"]
        self.lock = {"active": True, "campaign": "batch-2874", "phase": "prepared",
                     "identity": {"base_sha": "a" * 40, "candidate_sha": "d" * 40}}
        self.assertTrue(self.inspect()["resume_integration"])
        self.lock["campaign"] = "other"
        with self.assertRaisesRegex(Rejected, "Another campaign"):
            self.inspect()

    def test_moved_runtime_ref_fails_before_reading_contract(self):
        with patch("queue_support.github", return_value={"object": {"sha": "e" * 40}}) as api:
            with self.assertRaisesRegex(Rejected, "branch moved"):
                pinned_manifest(self.args.repo, self.args.workflow_sha, self.args.workflow_ref)
            self.assertEqual(1, api.call_count)


if __name__ == "__main__":
    unittest.main()
