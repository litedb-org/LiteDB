"""Validate timing evidence orchestration without running or timing a database."""
import contextlib
import importlib.util
import io
import json
import os
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

SPEC = importlib.util.spec_from_file_location(
    "shared_open_close", Path(__file__).resolve().parents[2] / "tools/SharedOpenCloseMeasurements/run.py")
MEASURE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MEASURE)


class SharedOpenCloseEvidenceTests(unittest.TestCase):
    def test_local_execution_is_refused_before_any_process_starts(self):
        with patch.dict(os.environ, {}, clear=True), patch.object(MEASURE.subprocess, "run") as run:
            with self.assertRaisesRegex(RuntimeError, "only in fork"):
                MEASURE.main()
            run.assert_not_called()

    def exercise(self, root, mutate=None):
        calls = []

        def sample(args, **kwargs):
            variant, sha = args[-2:]
            calls.append(variant)
            row = {"variant": variant, "sourceSha": sha, "cycles": 50,
                   "perCycleMs": 2 if variant == "baseline" else 1}
            if mutate:
                mutate(row)
            return SimpleNamespace(stdout=json.dumps(row))

        env = {"GITHUB_ACTIONS": "true", "GITHUB_REPOSITORY": "JKamsker/LiteDB",
               "GITHUB_STEP_SUMMARY": str(root / "step.md")}
        argv = ["run.py", "runner-base", "runner-head", str(root / "scratch"),
                str(root / "evidence"), "base-sha", "head-sha"]
        with patch.dict(os.environ, env, clear=True), patch.object(MEASURE.sys, "argv", argv), \
                patch.object(MEASURE.subprocess, "run", side_effect=sample), contextlib.redirect_stdout(io.StringIO()):
            MEASURE.main()
        return calls

    def test_serial_alternation_complete_samples_and_per_cycle_report(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            calls = self.exercise(root)
            self.assertEqual(calls, ["baseline", "candidate", "candidate", "baseline",
                                     "baseline", "candidate", "candidate", "baseline", "baseline", "candidate"])
            rows = [json.loads(line) for line in (root / "evidence/raw.jsonl").read_text().splitlines()]
            self.assertEqual(len(rows), 10)
            self.assertTrue(all(row["cycles"] == 50 for row in rows))
            self.assertEqual(json.loads((root / "evidence/summary.json").read_text()), {"baseline": 2, "candidate": 1})
            summary = (root / "step.md").read_text()
            self.assertIn("base-sha", summary)
            self.assertIn("head-sha", summary)
            self.assertIn("2.000", summary)
            self.assertIn("1.000", summary)

    def test_wrong_revision_partial_cycles_and_invalid_measurement_cannot_report_success(self):
        for key, value in [("sourceSha", "wrong-sha"), ("cycles", 49), ("perCycleMs", 0)]:
            with self.subTest(key=key), tempfile.TemporaryDirectory() as temp:
                root = Path(temp)
                with self.assertRaisesRegex(RuntimeError, "Incomplete"):
                    self.exercise(root, lambda row: row.update({key: value}))
                self.assertFalse((root / "evidence/summary.json").exists())


if __name__ == "__main__":
    unittest.main()
