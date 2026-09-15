"""The final-promotion CLI is verify-only and preserves complete inputs."""

from contextlib import contextmanager
import hashlib
import json
from pathlib import Path
import subprocess
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

import final_promotion
from state import Rejected


class FinalPromotionTests(unittest.TestCase):
    def args(self, root):
        ledger = {"schema_version": 1, "issues": {"100": {"tests": ["LiteDB.Test"]}}}
        digest = hashlib.sha256(json.dumps(
            ledger, sort_keys=True, separators=(",", ":"),
            ensure_ascii=False).encode()).hexdigest()
        return SimpleNamespace(
            repo="litedb-org/LiteDB", repository=root,
            baseline_run=101, candidate_run=202,
            base_sha="a" * 40, final_integration_sha="b" * 40,
            accepted_state_sha="c" * 40, accepted_ledger_sha256=digest,
            test_source_sha="a" * 40, evidence_definition_sha="d" * 40,
            grading_policy_sha="e" * 40, archive_dir=root / "archive",
            output=root / "verdict.json"), ledger

    def test_verify_only_orchestrator_passes_explicit_all_issue_identity(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            args, ledger = self.args(root)
            control = root / "control"
            control.mkdir()
            commands = []

            @contextmanager
            def checkout(*_arguments):
                yield control

            def retain(_control, destination, identity):
                destination.mkdir(parents=True)
                (destination / "promotion-identity.json").write_text(json.dumps(identity))
                return {"promotion-identity.json": {"sha256": "f" * 64, "bytes": 1}}

            def run_report(command, output):
                commands.append(command)
                output = Path(output)
                output.parent.mkdir(parents=True, exist_ok=True)
                if "collect_bugfix_full_ci.py" in command[1]:
                    output.write_text('{"accepted":true}')
                    run_id = command[command.index("--run-id") + 1]
                    raw = Path(command[command.index("--archive-dir") + 1]) / run_id
                    raw.mkdir()
                    (raw / "run.json").write_text("{}")
                else:
                    output.write_text(json.dumps({
                        "accepted": True, "outcome": "promotion_ready",
                        "errors": [], "blockers": [], "unexpected_passes": [],
                        "inconclusive_changes": [], "accepted_issues": [100],
                        "accepted_tests": ["LiteDB.Test"],
                        "accepted_test_case_count": 1,
                        "accepted_test_execution_count": 21,
                        "artifact_counts": {"baseline": 105, "candidate": 105},
                        "coverage_gaps": [{"issue": 2854}],
                        "architecture_limitations": ["historic label"],
                    }))

            raw = json.dumps(ledger).encode()
            with patch("final_promotion.fetch_identities"), \
                    patch("final_promotion.ledger_blob", return_value=(raw, ledger)), \
                    patch("final_promotion.worktree", checkout), \
                    patch("final_promotion.retain_capture_definition",
                          return_value={"workflow": {"sha256": "a" * 64}}), \
                    patch("final_promotion.retain_final_policy", side_effect=retain), \
                    patch("final_promotion.checked_report", side_effect=run_report):
                result = final_promotion.execute(args)
            self.assertEqual("verified", result["phase"])
            self.assertFalse(result["applies"])
            self.assertEqual({"baseline": 105, "candidate": 105},
                             result["artifact_counts"])
            self.assertEqual(3, len(commands))
            for command in commands[:2]:
                self.assertIn("final-promotion", command)
                self.assertNotIn("--issue", command)
                self.assertEqual(args.accepted_state_sha,
                                 command[command.index("--accepted-state-sha") + 1])
                self.assertEqual(args.accepted_ledger_sha256,
                                 command[command.index("--accepted-ledger-sha256") + 1])
            comparator = commands[2]
            self.assertIn("compare_bugfix_final_promotion.py", comparator[1])
            self.assertTrue((args.archive_dir / "accepted-tests.json").is_file())
            self.assertTrue((args.archive_dir / "final-promotion-verdict.json").is_file())
            self.assertTrue((args.archive_dir / "archive-manifest.json").is_file())

    def test_existing_archive_is_never_overwritten(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            args, _ = self.args(root)
            args.archive_dir.mkdir()
            with self.assertRaisesRegex(Rejected, "already exists"):
                final_promotion.execute(args)

    def test_parser_defaults_to_upstream_and_has_no_write_switch(self):
        parser = final_promotion.parser()
        options = {action.dest for action in parser._actions}
        self.assertNotIn("apply", options)
        args = parser.parse_args([
            "--baseline-run", "1", "--candidate-run", "2",
            "--base-sha", "a" * 40, "--final-integration-sha", "b" * 40,
            "--accepted-state-sha", "c" * 40,
            "--accepted-ledger-sha256", "d" * 64,
            "--test-source-sha", "a" * 40,
            "--evidence-definition-sha", "e" * 40,
            "--grading-policy-sha", "f" * 40, "--output", "result.json"])
        self.assertEqual("litedb-org/LiteDB", args.repo)


class LedgerBlobTests(unittest.TestCase):
    def test_reads_only_an_ordinary_bounded_git_blob(self):
        ledger = {"schema_version": 1, "issues": {"100": {}}}
        completed = subprocess.CompletedProcess([], 0, json.dumps(ledger).encode(), b"")
        with patch("final_promotion.git",
                   return_value="100644 blob deadbeef\taccepted-tests.json"), \
                patch("final_promotion.subprocess.run", return_value=completed):
            raw, value = final_promotion.ledger_blob("repo", "a" * 40)
        self.assertEqual(ledger, value)
        self.assertEqual(completed.stdout, raw)
        with patch("final_promotion.git", return_value="120000 blob bad\taccepted-tests.json"):
            with self.assertRaisesRegex(Rejected, "ordinary"):
                final_promotion.ledger_blob("repo", "a" * 40)


if __name__ == "__main__":
    unittest.main()
