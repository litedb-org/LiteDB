"""Protocol-only checks using fake workers; no database or latency measurements."""
import importlib.util
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

RUNNER = Path(__file__).resolve().parents[2] / "tools/SharedWriterTenureMeasurements/newcomer.py"
sys.path.insert(0, str(RUNNER.parent))
try:
    spec = importlib.util.spec_from_file_location("newcomer_measurements", RUNNER)
    measurements = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(measurements)
finally:
    sys.path.pop(0)


class NewcomerOrchestrationTests(unittest.TestCase):
    def test_local_execution_is_refused_before_arguments_or_processes(self):
        with patch.dict(os.environ, {}, clear=True), patch.object(measurements, "Worker") as worker:
            with self.assertRaisesRegex(SystemExit, "fork-CI only"):
                measurements.main()
            worker.assert_not_called()

    def test_fork_ci_still_refuses_other_platforms(self):
        with patch.dict(os.environ, {"CI": "true", "GITHUB_REPOSITORY": "JKamsker/LiteDB"}), \
                patch.object(measurements.platform, "machine", return_value="aarch64"):
            with self.assertRaisesRegex(SystemExit, "Linux x64"):
                measurements.main()

    def test_two_processes_alternate_fresh_attachments_after_real_pressure_preparation(self):
        events = []
        attached = set()
        instances = []

        class FakeWorker:
            def __init__(self, *args):
                self.slot = len(instances)
                instances.append(self)

            def read(self):
                return "ready"

            def exchange(self, line):
                events.append((self.slot, line))
                mode = line.split()[0]
                if mode == "acquire":
                    self_outer.assertNotIn(self.slot, attached)
                    attached.add(self.slot)
                    return json.dumps(dict(attachToAcquireMicroseconds=int(line.split()[1]) + 1,
                                           processId=100 + self.slot))
                if mode == "prepare":
                    self_outer.assertEqual(attached, {self.slot}, "only the holder is mapped before the next newcomer")
                    return json.dumps(dict(walPhysicalBytes=8192 * 140, updates=30, checkpoint=1000))
                if mode == "detach":
                    attached.remove(self.slot)
                    return "detached"
                raise AssertionError(line)

            def close(self):
                attached.discard(self.slot)

        self_outer = self
        with tempfile.TemporaryDirectory() as directory, \
                patch.object(measurements, "Worker", FakeWorker), patch.object(measurements, "run") as run:
            result = measurements.measure("candidate", "test.db", "false", Path(directory), count=4, warmup=2)
            self.assertEqual(attached, set())
            run.assert_called_once_with("candidate", "verify", "test.db", 6)
        self.assertEqual(len(instances), 2)
        self.assertEqual([slot for slot, cmd in events if cmd.startswith("acquire")], [0, 1, 0, 1, 0, 1, 0])
        self.assertEqual([s["turn"] for s in result["samples"]], [3, 4, 5, 6])
        self.assertEqual({s["processId"] for s in result["samples"]}, {100, 101})
        self.assertTrue(all(s["outgoing"]["checkpoint"] == 1000 for s in result["samples"]))
        self.assertEqual(result["p50Microseconds"], 5)
        self.assertEqual(result["p99Microseconds"], 7)

    def test_nearest_rank_p99_does_not_report_p95(self):
        self.assertEqual(measurements.percentile(list(reversed(range(1, 201))), .99), 198)

    def test_five_order_alternating_pairs_keep_durability_and_percentiles_separate(self):
        def fake_measure(runner, path, durable, output):
            factor = 1 if runner == "baseline" else 2
            return dict(durable=durable == "true", p50Microseconds=100 * factor, p99Microseconds=300 * factor)

        with tempfile.TemporaryDirectory() as directory:
            destination = Path(directory) / "evidence"
            argv = [str(RUNNER), "baseline", "candidate", str(Path(directory) / "scratch"), str(destination)]
            with patch.dict(os.environ, {"GITHUB_REPOSITORY": "JKamsker/LiteDB", "CI": "true"}), \
                    patch.object(measurements.platform, "system", return_value="Linux"), \
                    patch.object(measurements.platform, "machine", return_value="x86_64"), \
                    patch.object(sys, "argv", argv), patch.object(measurements, "run") as run, \
                    patch.object(measurements, "measure", side_effect=fake_measure), patch("builtins.print"):
                measurements.main()
            records = json.loads((destination / "newcomer-raw.json").read_text())
            self.assertEqual(len(records), 20)
            self.assertEqual(run.call_count, 20)
            for round_number in range(5):
                sample = [r for r in records if r["round"] == round_number]
                order = ["before", "after"] if round_number % 2 == 0 else ["after", "before"]
                self.assertEqual([r["variant"] for r in sample], order * 2)
                self.assertEqual({r["durable"] for r in sample}, {False, True})
            summary = json.loads((destination / "newcomer-summary.json").read_text())
            self.assertEqual(len(summary), 4)
            self.assertEqual([s["afterBeforeMedian"] for s in summary], [2] * 4)
            self.assertEqual({s["metric"] for s in summary}, {"p50Microseconds", "p99Microseconds"})


if __name__ == "__main__":
    unittest.main()
