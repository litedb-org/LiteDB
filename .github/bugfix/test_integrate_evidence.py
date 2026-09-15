"""Actual report identities and explicit matrix exclusions gate integration."""

import copy
import hashlib
import json
import unittest
from unittest.mock import patch

from evidence import MATRIX, event_for
from integrate_evidence import acceptance_evidence, validate_archived_evidence
from state import ROLES, Rejected, new_state
from test_artifacts import archive
from test_worker_runtime import runtime_fixture
from test_profiles import profile_fixture


class AcceptanceRevalidationTests(unittest.TestCase):
    def setUp(self):
        self.state = new_state("canary", 2874, "a" * 40, "b" * 40, "c" * 40)
        self.state.update(phase="ready", candidate_sha="d" * 40)
        self.data = {}
        self.events = {}
        self.compatibility = True
        self.production = {"artifact": "bugfix-production", "artifact_id": 90,
                           "artifact_sha256": "8" * 64, "report_sha256": "9" * 64}
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
            runtime, proof = runtime_fixture("gpt-5.6-sol")
            metadata.update(runtime)
            self.data[(index, event["artifact"])] = {"result.json": result, "metadata.json": metadata,
                                                   "runtime-proof.json": proof}
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
            return {"id": run_id, "status": "completed", "conclusion": "success", "head_sha": self.events[run_id].get("check_workflow_sha", "c" * 40),
                    "event": "workflow_dispatch", "path": ".github/workflows/" + workflow}

        with patch("integrate_evidence.github", side_effect=api), \
                patch("integrate_evidence.production_evidence", return_value=(
                    self.production, getattr(self, "production_data", b"production proof"))), \
                patch("integrate_evidence.download", side_effect=lambda repo, item: archive(self.data[(item["run_id"], item["name"])])):
            return acceptance_evidence("owner/repo", self.state)

    def use_profile(self, kind, compressed=False):
        self.state["acceptance_profile"] = profile_fixture()
        if compressed:
            self.state["protocol"] = "compressed-v1"
            for event in self.events.values():
                event["protocol"] = "compressed-v1"
        event = self.state["evidence"][kind]
        event.update(acceptance_profile=self.state["acceptance_profile"], profile_complete=True,
                     production=copy.deepcopy(self.production))
        if kind == "acceptance":
            self.state["check_definition"] = {"workflow_sha": "e" * 40}
            event["check_workflow_sha"] = "e" * 40
        report = self.data[(event["run_id"], MATRIX[0])]["verdict.json"]
        report.update(acceptance_profile=self.state["acceptance_profile"])
        if compressed:
            report["protocol"] = "compressed-v1"
            for earlier in ("baseline",):
                self.data[(self.state["evidence"][earlier]["run_id"], MATRIX[0])]["verdict.json"]["protocol"] = "compressed-v1"
        report["workflow_sha"] = event.get("check_workflow_sha", event["workflow_sha"])
        for current in self.state["evidence"].values():
            verdict = self.data[(current["run_id"], MATRIX[0])]["verdict.json"]
            current["report_sha256"] = hashlib.sha256(json.dumps(verdict).encode()).hexdigest()
        event["matrix"] = [{"artifact": MATRIX[0], "environment": event["environment"],
                            "report_sha256": event["report_sha256"]}]

    def test_complete_evidence_is_retained(self):
        files = self.check()
        self.assertEqual(12, len([name for name in files if name.endswith(".zip")]))
        validate_archived_evidence(files, self.state)

    def test_changed_prepared_raw_artifact_is_rejected_offline(self):
        files = self.check()
        files["acceptance/run-1/bugfix-check-ubuntu-latest-net8.0.zip"] = b"changed"
        with self.assertRaises(Rejected):
            validate_archived_evidence(files, self.state)

    def test_archived_run_and_recorded_artifact_identities_are_exact(self):
        files = self.check()
        run = json.loads(files["acceptance/run-1/run.json"])
        run["id"] = 999
        files["acceptance/run-1/run.json"] = json.dumps(run).encode()
        with self.assertRaisesRegex(Rejected, "run ID"):
            validate_archived_evidence(files, self.state)

        files = self.check()
        self.state["evidence"]["acceptance"]["matrix"][0]["artifact_sha256"] = "0" * 64
        with self.assertRaisesRegex(Rejected, "artifact changed"):
            validate_archived_evidence(files, self.state)

    def test_archived_review_definition_cannot_change(self):
        files = self.check()
        self.state["reviews"]["behavior"]["check_workflow_sha"] = "e" * 40
        with self.assertRaisesRegex(Rejected, "original worker definition"):
            validate_archived_evidence(files, self.state)

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

    def test_compressed_profile_needs_only_one_candidate_run_and_three_reviews(self):
        self.use_profile("broad", compressed=True)
        self.compatibility = False
        del self.state["evidence"]["focused"]
        del self.state["evidence"]["acceptance"]
        files = self.check()
        self.assertEqual(6, len([name for name in files if name.endswith(".zip")]))
        self.assertIn("acceptance/run-3/bugfix-production.zip", files)

    def test_profiled_prepared_production_proof_revalidates_offline(self):
        self.use_profile("broad", compressed=True)
        report = {"schema_version": 1, "issue": self.state["issue"], "base_sha": self.state["base_sha"],
                  "candidate_sha": self.state["candidate_sha"], "workflow_sha": self.state["workflow_sha"],
                  "acceptance_profile": self.state["acceptance_profile"], "accepted": True,
                  "production_build": True, "compatibility": self.state["acceptance_profile"]["compatibility"]}
        raw = json.dumps(report).encode()
        self.production_data = archive({"production.json": raw})
        self.production = {"artifact": "bugfix-production", "artifact_id": 90,
                           "artifact_sha256": hashlib.sha256(self.production_data).hexdigest(),
                           "report_sha256": hashlib.sha256(raw).hexdigest()}
        self.state["evidence"]["broad"]["production"] = copy.deepcopy(self.production)
        files = self.check()
        validate_archived_evidence(files, self.state)

    def test_audited_legacy_revalidation_preserves_original_reviews_and_one_new_lane(self):
        self.use_profile("acceptance")
        self.compatibility = False
        files = self.check()
        self.assertIn("acceptance/run-4/bugfix-production.zip", files)
        self.assertEqual("c" * 40, self.state["reviews"]["behavior"]["workflow_sha"])
        self.assertNotIn("acceptance/run-4/bugfix-check-windows-latest-net8.0.zip", files)

    def test_profile_cannot_omit_build_lane_or_change_protocol(self):
        self.use_profile("broad", compressed=True)
        event = self.state["evidence"]["broad"]
        event["production"]["report_sha256"] = "0" * 64
        with self.assertRaisesRegex(Rejected, "production evidence changed"):
            self.check()
        event["production"] = self.production
        event["matrix"] = []
        with self.assertRaisesRegex(Rejected, "lanes are missing"):
            self.check()
        event["protocol"] = "legacy-six-lane-v1"
        with self.assertRaisesRegex(Rejected, "protocol changed"):
            self.check()


if __name__ == "__main__":
    unittest.main()
