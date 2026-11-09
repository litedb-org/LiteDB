"""Exercise CI orchestration with fake measurements; never launch a benchmark."""
import importlib.util
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

RUNNER = Path(__file__).resolve().parents[2] / "tools/SharedWriterTenureMeasurements/run.py"
spec = importlib.util.spec_from_file_location("tenure_measurements", RUNNER)
measurements = importlib.util.module_from_spec(spec)
spec.loader.exec_module(measurements)


class WriterTenureOrchestrationTests(unittest.TestCase):
    def test_local_execution_is_refused_before_subprocess_or_arguments(self):
        with patch.dict(os.environ, {}, clear=True), patch.object(measurements, "run") as run:
            with self.assertRaisesRegex(SystemExit, "fork-CI only"):
                measurements.main()
            run.assert_not_called()

    def test_five_alternating_pairs_keep_modes_durability_and_ratios_separate(self):
        calls = []

        def fake_run(runner, mode, *args):
            calls.append((runner, mode, args))
            if mode != "single":
                return ""
            return json.dumps(dict(mode=mode, durable=args[1] == "true", count=int(args[2]),
                                   transactionsPerSecond=100 if runner == "baseline" else 200))

        def fake_alternating(runner, path, durable):
            return dict(mode="alternating", durable=durable == "true", count=200,
                        p50Microseconds=100 if runner == "baseline" else 50,
                        p95Microseconds=200 if runner == "baseline" else 100)

        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "evidence"
            argv = [str(RUNNER), "baseline", "candidate", str(Path(directory) / "scratch"), str(output)]
            with patch.dict(os.environ, {"GITHUB_REPOSITORY": "JKamsker/LiteDB", "CI": "true"}), \
                    patch.object(sys, "argv", argv), patch.object(measurements, "run", side_effect=fake_run), \
                    patch.object(measurements, "alternating", side_effect=fake_alternating), patch("builtins.print"):
                measurements.main()
            records = json.loads((output / "paired.json").read_text())
            self.assertEqual(len(records), 40)
            for round_number in range(5):
                sample = [r for r in records if r["round"] == round_number]
                expected = ["before", "after"] if round_number % 2 == 0 else ["after", "before"]
                self.assertEqual([r["variant"] for r in sample], expected * 4)
                self.assertEqual({r["durable"] for r in sample}, {False, True})
                self.assertEqual({r["mode"] for r in sample}, {"single", "alternating"})
            summary = json.loads((output / "summary.json").read_text())
            self.assertEqual(len(summary), 6)
            self.assertEqual([r["afterBeforeMedian"] for r in summary], [2, .5, .5] * 2)
            self.assertEqual(sum(mode == "verify" for _, mode, _ in calls), 20)


if __name__ == "__main__":
    unittest.main()
