"""Only authenticated budget/accounting decisions can suppress retry consumption."""

import copy
from datetime import datetime, timezone
import hashlib
import io
import json
import unittest
from unittest.mock import patch
import zipfile

from budget_cooldown import notice
from state import Rejected


class BudgetCooldownTests(unittest.TestCase):
    def setUp(self):
        self.run = {"id": 123, "run_attempt": 1, "head_sha": "a" * 40, "status": "completed",
                    "created_at": "2026-09-15T00:00:00Z"}
        self.report = {"schema_version": 1, "run_id": 123, "run_attempt": 1, "workflow_sha": "a" * 40,
                       "observed_at": "2026-09-15T01:00:00Z", "exceeded": True, "threshold": 5000, "total": 5001}
        self.jobs = [{"name": "agent", "conclusion": "skipped"}, {"name": "conclusion", "conclusion": "success"}]
        self.artifacts = [{"id": 7, "name": "bugfix-budget", "expired": False}]
        self.now = datetime(2026, 9, 15, 2, tzinfo=timezone.utc).timestamp()

    def check(self, report=None, artifacts=None, jobs=None):
        report = report or self.report
        raw_report = json.dumps(report).encode()
        stream = io.BytesIO()
        with zipfile.ZipFile(stream, "w") as archive:
            archive.writestr("budget-status.json", raw_report)
        raw = stream.getvalue()
        with patch("budget_cooldown.download", return_value=raw), patch("budget_cooldown.time.time", return_value=self.now):
            result = notice("owner/repo", self.run, self.artifacts if artifacts is None else artifacts,
                            self.jobs if jobs is None else jobs)
        return result, raw, raw_report

    def test_positive_quota_proof_binds_raw_hashes_and_conservative_reset(self):
        result, archive, raw = self.check()
        self.assertEqual(hashlib.sha256(archive).hexdigest(), result["artifact_sha256"])
        self.assertEqual(hashlib.sha256(raw).hexdigest(), result["report_sha256"])
        self.assertEqual(7, result["artifact_id"])
        self.assertEqual(self.now + 23 * 3600, result["resume_after"])

    def test_accounting_unavailable_is_distinct_short_cooldown(self):
        report = {key: value for key, value in self.report.items() if key not in ("exceeded", "threshold", "total")}
        report["status"] = "accounting_unavailable"
        result, _, _ = self.check(report)
        self.assertEqual("accounting_unavailable", result["reason"])
        self.assertEqual(self.now - 3600 + 900, result["resume_after"])

    def test_generic_skip_without_attestation_is_not_a_budget_decision(self):
        self.assertIsNone(self.check(artifacts=[])[0])

    def test_wrong_identity_bad_numbers_and_timestamps_fail_closed(self):
        changes = (("run_id", 124), ("run_attempt", 2), ("workflow_sha", "b" * 40),
                   ("exceeded", False), ("threshold", 0), ("threshold", True), ("total", 4999),
                   ("total", float("nan")), ("total", float("inf")),
                   ("observed_at", "2026-09-14T23:59:59Z"), ("observed_at", "2026-09-16T00:00:00Z"))
        for key, value in changes:
            with self.subTest(key=key, value=value), self.assertRaises(Rejected):
                self.check({**self.report, key: value})

    def test_wrong_jobs_ambiguous_or_extra_fields_fail_closed(self):
        for jobs in ([{"name": "agent", "conclusion": "success"}, self.jobs[1]], self.jobs[:1]):
            with self.assertRaises(Rejected):
                self.check(jobs=jobs)
        with self.assertRaises(Rejected):
            self.check(artifacts=self.artifacts * 2)
        with self.assertRaises(Rejected):
            self.check({**self.report, "status": "accounting_unavailable"})


if __name__ == "__main__":
    unittest.main()
