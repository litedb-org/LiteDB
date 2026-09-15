"""Actual report identities and explicit matrix exclusions gate integration."""

import copy
import hashlib
import json
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

from evidence import MATRIX, event_for
from integrate_evidence import acceptance_evidence, original_matrix_evidence
from state import ROLES, Rejected, new_state
from test_artifacts import archive


class MatrixPolicyTests(unittest.TestCase):
    def setUp(self):
        self.state = new_state("canary", 2874, "a" * 40, "b" * 40, "c" * 40)
        self.state.update(phase="ready", candidate_sha="d" * 40)
        self.args = SimpleNamespace(repo="owner/repo", baseline_run=111, candidate_run=222,
                                    evidence_definition_sha="e" * 40, grading_policy_sha="f" * 40)
        self.quarantine = {"schema_version": 1, "expected_original_jobs": 112, "expected_remaining_jobs": 109,
                           "quarantines": [{"issue": 2854, "status": "unverified"}]}
        self.report = {"schema_version": 1, "accepted": True, "outcome": "behavior_correct",
                       "errors": [], "blockers": [], "unexpected_passes": [], "coverage_gaps": self.quarantine["quarantines"],
                       "provenance": {"issue": 2874, "baseline_run_id": 111, "candidate_run_id": 222,
                                      "base_sha": "a" * 40, "candidate_sha": "d" * 40,
                                      "evidence_definition_sha": "e" * 40,
                                      "workflow_path": ".github/workflows/bugfix-full-ci.yml"}}
        self.commands = []

    def check(self):
        with tempfile.TemporaryDirectory() as directory:
            control = Path(directory) / "control"
            policy = control / ".github/bugfix/full-ci-quarantine.json"
            policy.parent.mkdir(parents=True)
            data = json.dumps(self.quarantine).encode()
            policy.write_bytes(data)
            self.report["provenance"]["quarantine_sha256"] = hashlib.sha256(data).hexdigest()
            normalization = control / "scripts/bugfix/failure-normalization.json"
            normalization.parent.mkdir(parents=True)
            normalization.write_bytes(b'{"schema_version":1,"tests":{}}')
            self.report["provenance"]["failure_normalization_sha256"] = hashlib.sha256(normalization.read_bytes()).hexdigest()
            for name in ("failure_normalization.py", "trx.py"):
                (normalization.parent / name).write_text("# trusted normalization module", encoding="utf-8")
            scripts = control / ".github/scripts"
            scripts.mkdir()
            for name in ("collect_bugfix_full_ci.py", "compare_bugfix_full_ci.py"):
                (scripts / name).write_text("# trusted grading script", encoding="utf-8")

            def command(arguments):
                self.commands.append(arguments)
                output = Path(arguments[arguments.index("--output") + 1])
                if "collect_bugfix_full_ci.py" in arguments[1]:
                    run_id = arguments[arguments.index("--run-id") + 1]
                    root = Path(arguments[arguments.index("--archive-dir") + 1]) / run_id
                    root.mkdir(parents=True)
                    (root / "run.json").write_text(json.dumps({"id": int(run_id)}))
                    output.write_text(json.dumps({"schema_version": 1, "accepted": True}))
                else:
                    output.write_text(json.dumps(self.report))

            with patch("integrate_evidence.run", side_effect=command):
                return original_matrix_evidence(self.args, self.state, control, Path(directory) / "output")

    def test_recollects_both_runs_and_persists_capture_policy_and_quarantine(self):
        report, files = self.check()
        self.assertEqual(3, len(self.commands))
        self.assertEqual("a" * 40, self.commands[0][self.commands[0].index("--source-sha") + 1])
        self.assertEqual("d" * 40, self.commands[1][self.commands[1].index("--source-sha") + 1])
        self.assertEqual("baseline", self.commands[0][self.commands[0].index("--role") + 1])
        self.assertEqual("candidate", self.commands[1][self.commands[1].index("--role") + 1])
        self.assertTrue(all("--failure-normalization" in command for command in self.commands))
        self.assertEqual("f" * 40, report["provenance"]["grading_policy_sha"])
        self.assertEqual(self.report["coverage_gaps"], report["coverage_gaps"])
        self.assertIn("original-matrix/quarantine.json", files)
        provenance = json.loads(files["original-matrix/grading-provenance.json"])
        self.assertEqual("e" * 40, provenance["capture_definition_sha"])
        self.assertEqual("f" * 40, provenance["grading_policy_sha"])

    def test_any_additional_harness_failure_or_missing_job_blocks(self):
        for field in ("errors", "blockers", "unexpected_passes"):
            with self.subTest(field=field):
                self.report[field] = ["unreviewed missing or failed job"]
                with self.assertRaises(Rejected):
                    self.check()
                self.report[field] = []

    def test_extra_quarantine_or_stale_capture_is_rejected(self):
        self.report["coverage_gaps"] = self.quarantine["quarantines"] + [{"issue": 9999}]
        with self.assertRaisesRegex(Rejected, "exclusions"):
            self.check()
        self.report["coverage_gaps"] = self.quarantine["quarantines"]
        self.report["provenance"]["evidence_definition_sha"] = "1" * 40
        with self.assertRaisesRegex(Rejected, "evidence_definition_sha"):
            self.check()


class AcceptanceRevalidationTests(unittest.TestCase):
    def setUp(self):
        self.state = new_state("canary", 2874, "a" * 40, "b" * 40, "c" * 40)
        self.state.update(phase="ready", candidate_sha="d" * 40)
        self.data = {}
        self.events = {}
        self.compatibility = True
        for index, kind in enumerate(("baseline", "focused", "broad", "acceptance"), 1):
            event = event_for(self.state, kind, index, outcome="bug_present" if kind == "baseline" else
                              ("behavior_correct" if kind == "focused" else "pass"),
                              environment="linux-x64-net8.0", artifact=MATRIX[0])
            if kind == "baseline":
                event["candidate_sha"] = None
            names = MATRIX if kind == "acceptance" else (MATRIX[0],)
            lanes = []
            for name in names:
                os_name = "linux" if "ubuntu" in name else ("windows" if "windows" in name else "macos")
                environment = f"{os_name}-x64-{name.rsplit('-', 1)[1]}"
                verdict = {key: event[key] for key in ("issue", "base_sha", "candidate_sha", "test_source_sha", "workflow_sha")}
                verdict.update(schema_version=1, accepted=True, level=kind, environment=environment,
                               outcome="bug_present" if kind == "baseline" else "behavior_correct")
                scope = {"accepted": True, "outcome": "scope_verified", "provenance":
                         {key: event[key] for key in ("issue", "base_sha", "candidate_sha")}}
                self.data[(index, name)] = {"verdict.json": verdict, "scope.json": scope}
                digest = hashlib.sha256(json.dumps(verdict).encode()).hexdigest()
                lanes.append({"artifact": name, "environment": environment, "report_sha256": digest})
                if name == MATRIX[0]:
                    event["report_sha256"] = digest
            if kind == "acceptance":
                event["matrix"] = lanes
            self.state["evidence"][kind] = event
            self.events[index] = event
        for index, role in enumerate(ROLES, 10):
            event = event_for(self.state, "review", index, role=role, outcome="pass", findings=[],
                              environment="independent-agent", artifact=f"review-{role}")
            result = {key: event[key] for key in ("schema_version", "issue", "base_sha", "candidate_sha", "test_source_sha", "role")}
            result.update(verdict="pass", findings=[], coverage=["Reviewed adjacent behavior"])
            digest = hashlib.sha256(json.dumps(result).encode()).hexdigest()
            metadata = {key: result[key] for key in ("schema_version", "issue", "base_sha", "candidate_sha", "test_source_sha", "role")}
            metadata.update(kind="review", workflow_sha="c" * 40, run_id=str(index), result_sha256=digest,
                            configured_model="gpt-5.6-sol", configured_reasoning_effort="high")
            self.data[(index, event["artifact"])] = {"result.json": result, "metadata.json": metadata}
            event["report_sha256"] = digest
            self.state["reviews"][role] = event
            self.events[index] = event

    def check(self):
        def api(repo, path):
            run_id = int(path.split("/")[2])
            if "/artifacts" in path:
                records = [{"id": index, "name": name, "run_id": run_id, "expired": False}
                           for index, (run, name) in enumerate(self.data) if run == run_id]
                return {"total_count": len(records), "artifacts": records}
            if "/jobs" in path:
                return {"total_count": 1, "jobs": [{"name": "compatibility", "conclusion": "success" if self.compatibility else "skipped"}]}
            workflow = "bugfix-validate.lock.yml" if self.events[run_id]["kind"] == "review" else "bugfix-check.yml"
            return {"status": "completed", "conclusion": "success", "head_sha": "c" * 40,
                    "event": "workflow_dispatch", "path": ".github/workflows/" + workflow}

        with patch("integrate_evidence.github", side_effect=api), \
                patch("integrate_evidence.download", side_effect=lambda repo, item: archive(self.data[(item["run_id"], item["name"])])):
            return acceptance_evidence("owner/repo", self.state)

    def test_complete_evidence_is_retained(self):
        files = self.check()
        self.assertEqual(12, len([name for name in files if name.endswith(".zip")]))

    def test_missing_compatibility_or_review_cannot_accept(self):
        self.compatibility = False
        with self.assertRaisesRegex(Rejected, "compatibility"):
            self.check()
        self.compatibility = True
        del self.state["reviews"]["lifecycle"]
        with self.assertRaisesRegex(Rejected, "three"):
            self.check()

    def test_changed_previously_accepted_report_rejected(self):
        self.data[(3, MATRIX[0])]["verdict.json"]["new_field"] = "changed after original acceptance"
        with self.assertRaisesRegex(Rejected, "report changed"):
            self.check()


if __name__ == "__main__":
    unittest.main()
