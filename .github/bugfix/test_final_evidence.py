"""Final-matrix evidence retains both reviewed harness overlays and raw inputs."""

import hashlib
import json
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, patch

from final_evidence import (CAPTURE_DEFINITION_PATHS, FINAL_POLICY_PATHS,
                            original_matrix_evidence, retain_capture_definition,
                            retain_final_policy)
from integrate import finish
from state import Rejected, new_state


def git_blob_sha1(data):
    return hashlib.sha1(b"blob " + str(len(data)).encode() + b"\0" + data).hexdigest()


class MatrixPolicyTests(unittest.TestCase):
    def setUp(self):
        self.state = new_state("canary", 2874, "a" * 40, "b" * 40, "c" * 40)
        self.state.update(phase="ready", candidate_sha="d" * 40)
        self.args = SimpleNamespace(repo="owner/repo", baseline_run=111,
                                    candidate_run=222,
                                    evidence_definition_sha="e" * 40,
                                    grading_policy_sha="f" * 40)
        self.quarantine = {
            "schema_version": 1,
            "expected_original_jobs": 112,
            "expected_remaining_jobs": 109,
            "quarantines": [{"issue": 2854, "status": "unverified"}],
        }
        self.report = {
            "schema_version": 1,
            "accepted": True,
            "outcome": "behavior_correct",
            "errors": [],
            "blockers": [],
            "unexpected_passes": [],
            "inconclusive_changes": [],
            "coverage_gaps": self.quarantine["quarantines"],
            "provenance": {
                "issue": 2874,
                "baseline_run_id": 111,
                "candidate_run_id": 222,
                "base_sha": "a" * 40,
                "candidate_sha": "d" * 40,
                "evidence_definition_sha": "e" * 40,
                "workflow_path": ".github/workflows/bugfix-full-ci.yml",
            },
        }
        self.commands = []
        self.overlay_mutation = {}
        self.corrupt_issue = None

    def write_overlay(self, root, issue, filename, source_name):
        effective = f"// reviewed #{issue} harness correction\n".encode()
        manifest = {
            "schema_version": 1,
            "id": f"issue-{issue}-reviewed-harness-correction",
            "issue": issue,
            "source_path": f"LiteDB.ReproRunner/Repros/Issue_{issue}/Source.cs",
            "overlay_path": f".github/bugfix/{source_name}",
            "original_git_blob_sha1": "1" * 40,
            "original_blob_sha256": "2" * 64,
            "effective_git_blob_sha1": git_blob_sha1(effective),
            "effective_blob_sha256": hashlib.sha256(effective).hexdigest(),
            "dependency_blobs": {},
            "workload": {"attempts": 3},
        }
        if issue == 2794:
            manifest.update(wait_timeout_seconds=60, ready_timeout_seconds=15,
                            completion_timeout_seconds=90)
        else:
            manifest.update(primary_failure={"error_code": "PRIMARY"},
                            secondary_failure={"error_code": "SECONDARY"})
        raw = json.dumps(manifest).encode()
        (root / filename).write_bytes(raw)
        changed = b"changed" if self.corrupt_issue == issue else b""
        (root / source_name).write_bytes(effective + changed)
        return manifest, raw

    def check(self):
        with tempfile.TemporaryDirectory() as directory:
            control = Path(directory) / "control"
            bugfix = control / ".github/bugfix"
            bugfix.mkdir(parents=True)
            quarantine_raw = json.dumps(self.quarantine).encode()
            (bugfix / "full-ci-quarantine.json").write_bytes(quarantine_raw)
            self.overlay_2794, raw_2794 = self.write_overlay(
                bugfix, 2794, "issue-2794-harness-overlay.json",
                "issue-2794-worker-overlay.cs")
            self.overlay_2825, raw_2825 = self.write_overlay(
                bugfix, 2825, "issue-2825-harness-overlay.json",
                "issue-2825-classifier-overlay.cs")
            for prefix, manifest, raw in (
                    ("harness_overlay", self.overlay_2794, raw_2794),
                    ("issue_2825_harness_overlay", self.overlay_2825, raw_2825)):
                self.report["provenance"][prefix + "_manifest_sha256"] = \
                    hashlib.sha256(raw).hexdigest()
                self.report["provenance"][prefix] = {
                    **{key: value for key, value in manifest.items()
                       if key != "schema_version"},
                    "evidence_definition_sha": self.args.evidence_definition_sha,
                }
            self.report["provenance"].update(self.overlay_mutation)
            self.report["provenance"]["quarantine_sha256"] = \
                hashlib.sha256(quarantine_raw).hexdigest()
            policy = control / "scripts/bugfix"
            policy.mkdir(parents=True)
            normalization = policy / "failure-normalization.json"
            normalization.write_bytes(b'{"schema_version":1,"tests":{}}')
            self.report["provenance"]["failure_normalization_sha256"] = \
                hashlib.sha256(normalization.read_bytes()).hexdigest()
            baseline_policy = policy / "known-failure-classes.json"
            baseline_policy.write_bytes(b'{"classes":[],"skipped_tests":[]}')
            self.report["provenance"]["baseline_policy_sha256"] = \
                hashlib.sha256(baseline_policy.read_bytes()).hexdigest()
            for name in ("failure_normalization.py", "trx.py"):
                (policy / name).write_text("# trusted normalization module",
                                           encoding="utf-8")
            scripts = control / ".github/scripts"
            scripts.mkdir()
            for name in ("collect_bugfix_full_ci.py", "compare_bugfix_full_ci.py",
                         "apply_issue_2794_harness_overlay.py",
                         "apply_issue_2825_harness_overlay.py"):
                (scripts / name).write_text("# trusted grading script", encoding="utf-8")

            def command(arguments):
                self.commands.append(arguments)
                output = Path(arguments[arguments.index("--output") + 1])
                if "collect_bugfix_full_ci.py" in arguments[1]:
                    run_id = arguments[arguments.index("--run-id") + 1]
                    root = Path(arguments[arguments.index("--archive-dir") + 1]) / run_id
                    root.mkdir(parents=True)
                    (root / "run.json").write_text(json.dumps({"id": int(run_id)}))
                    output.write_text(json.dumps({"schema_version": 1,
                                                  "accepted": True}))
                else:
                    output.write_text(json.dumps(self.report))

            with patch("final_evidence.run", side_effect=command):
                return original_matrix_evidence(
                    self.args, self.state, control, Path(directory) / "output")

    def test_recollects_runs_and_persists_both_overlay_contracts(self):
        report, files = self.check()
        self.assertEqual(3, len(self.commands))
        self.assertEqual("a" * 40,
                         self.commands[0][self.commands[0].index("--source-sha") + 1])
        self.assertEqual("d" * 40,
                         self.commands[1][self.commands[1].index("--source-sha") + 1])
        for command in self.commands:
            for option in ("--harness-overlay-manifest",
                           "--issue-2825-harness-overlay-manifest"):
                self.assertTrue(Path(command[command.index(option) + 1]).is_absolute())
        self.assertEqual("f" * 40, report["provenance"]["grading_policy_sha"])
        provenance = json.loads(files["original-matrix/grading-provenance.json"])
        self.assertEqual(self.report["provenance"]["harness_overlay"],
                         provenance["harness_overlay"])
        self.assertEqual(self.report["provenance"]["issue_2825_harness_overlay"],
                         provenance["issue_2825_harness_overlay"])
        self.assertEqual(self.overlay_2825, json.loads(
            files["original-matrix/issue-2825-harness-overlay-manifest.json"]))
        self.assertEqual(self.overlay_2825["effective_blob_sha256"], hashlib.sha256(
            files["original-matrix/issue-2825-classifier-overlay.cs"]).hexdigest())
        for issue in (2794, 2825):
            name = f".github/scripts/apply_issue_{issue}_harness_overlay.py"
            retained = f"original-matrix/apply_issue_{issue}_harness_overlay.py"
            self.assertEqual(provenance["scripts"][name],
                             hashlib.sha256(files[retained]).hexdigest())

    def test_stale_overlay_hash_or_summary_rejected(self):
        for key, value in (
                ("harness_overlay_manifest_sha256", "0" * 64),
                ("harness_overlay", {"issue": 2794}),
                ("issue_2825_harness_overlay_manifest_sha256", "0" * 64),
                ("issue_2825_harness_overlay", {"issue": 2825})):
            with self.subTest(key=key):
                self.overlay_mutation = {key: value}
                with self.assertRaisesRegex(Rejected, "provenance mismatch"):
                    self.check()

    def test_modified_effective_overlay_rejected_before_remote_collection(self):
        for issue in (2794, 2825):
            with self.subTest(issue=issue):
                self.corrupt_issue = issue
                with self.assertRaisesRegex(Rejected, "overlay bytes changed"):
                    self.check()
                self.assertEqual([], self.commands)
                self.corrupt_issue = None

    def test_overlay_identity_survives_in_permanent_pass_ledger(self):
        report, _ = self.check()
        report["target_jobs"] = ["required matrix lane"]
        lock = {"token": "lock-token", "phase": "prepared", "active": True}
        store = Mock()
        store.require_lock.return_value = (lock, "1" * 40)
        store.read_at.side_effect = [self.state, None]
        finish(store, self.state, lock, report,
               {"regressions": [], "controls": []}, "9" * 40)
        ledger = json.loads(store.commit.call_args.args[1]["accepted-tests.json"])
        self.assertEqual(report["provenance"],
                         ledger["issues"]["2874"]["matrix_provenance"])

    def test_failure_or_unapproved_coverage_gap_blocks(self):
        for field in ("errors", "blockers", "unexpected_passes",
                      "inconclusive_changes"):
            with self.subTest(field=field):
                self.report[field] = ["unreviewed missing or failed job"]
                with self.assertRaises(Rejected):
                    self.check()
                self.report[field] = []
        self.report["coverage_gaps"] = self.quarantine["quarantines"] + [
            {"issue": 9999}]
        with self.assertRaisesRegex(Rejected, "exclusions"):
            self.check()

    def test_stale_capture_definition_is_rejected(self):
        self.report["provenance"]["evidence_definition_sha"] = "1" * 40
        with self.assertRaisesRegex(Rejected, "evidence_definition_sha"):
            self.check()


class FinalPolicyArchiveTests(unittest.TestCase):
    def test_retains_both_overlay_verifiers_manifests_and_effective_sources(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            control, destination = root / "control", root / "retained"
            for name in FINAL_POLICY_PATHS:
                path = control.joinpath(*Path(name).parts)
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes((name + "\n").encode())
            manifest = retain_final_policy(control, destination,
                                           {"accepted_state_sha": "a" * 40})
            for issue in (2794, 2825):
                self.assertIn(
                    f".github/scripts/apply_issue_{issue}_harness_overlay.py",
                    manifest)
                self.assertIn(
                    f".github/bugfix/issue-{issue}-harness-overlay.json",
                    manifest)
            self.assertTrue((destination / "promotion-identity.json").is_file())
            self.assertTrue((destination / "policy-manifest.json").is_file())

    def test_retains_capture_workflow_blobs_from_evidence_commit(self):
        import subprocess
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            subprocess.run(["git", "-C", str(root), "init", "--quiet"], check=True)
            subprocess.run(["git", "-C", str(root), "config", "user.email",
                            "test@example.com"], check=True)
            subprocess.run(["git", "-C", str(root), "config", "user.name",
                            "Test"], check=True)
            for name in CAPTURE_DEFINITION_PATHS:
                path = root.joinpath(*Path(name).parts)
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes((name + "\n").encode())
            subprocess.run(["git", "-C", str(root), "add", "."], check=True)
            subprocess.run(["git", "-C", str(root), "commit", "--quiet",
                            "-m", "capture"], check=True)
            sha = subprocess.run(["git", "-C", str(root), "rev-parse", "HEAD"],
                                 check=True, capture_output=True,
                                 text=True).stdout.strip()
            destination = root / "retained"
            manifest = retain_capture_definition(root, sha, destination)
            self.assertEqual(set(CAPTURE_DEFINITION_PATHS), set(manifest))
            self.assertTrue((destination / "capture-manifest.json").is_file())


if __name__ == "__main__":
    unittest.main()
