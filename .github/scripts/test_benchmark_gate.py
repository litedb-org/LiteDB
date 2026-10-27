"""Synthetic timings only: these tests never run a workload or benchmark."""
import copy
from contextlib import redirect_stdout
import importlib.util
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

import benchmark_gate as gate


ROOT = Path(__file__).resolve().parents[2]
HARNESS = ROOT / "tools/Ab5021Benchmarks"
CONFIG = json.loads((HARNESS / "gate.json").read_text())
SPEC = importlib.util.spec_from_file_location("benchmark_runner", HARNESS / "run.py")
RUNNER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(RUNNER)


class BenchmarkGateTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.output = Path(self.temp.name)
        self.config = copy.deepcopy(CONFIG)
        for config in RUNNER.CONFIGS:
            for number in range(1, 7):
                scenarios = dict(CONFIG["scenarios"])
                if config == "dev-default":
                    scenarios.update(CONFIG["scalingScenarios"])
                result = dict(config=config, round=number, assemblySha256="a" * 64,
                              candidateCommit="b" * 40, runnerOS="Linux",
                              framework=".NETCoreApp,Version=v8.0", runtime=".NET 8.0.0",
                              samples=[dict(scenario=name, milliseconds=100.0,
                                            operations=rule["operations"], dataBytes=8192, directoryBytes=16384)
                                       for name, rule in scenarios.items()])
                self.path(config, number).write_text(json.dumps(result))
        self.env = patch.dict(os.environ, {"BENCHMARK_CANDIDATE_SHA": "b" * 40,
                                         "GITHUB_STEP_SUMMARY": str(self.output / "synthetic-summary.md"),
                                         "GITHUB_OUTPUT": str(self.output / "synthetic-outputs.txt")})
        self.env.start()
        self.addCleanup(self.env.stop)

    def path(self, config, number):
        return self.output / f"round-{number}-{config}.json"

    def change(self, config, number, mutate):
        path = self.path(config, number)
        result = json.loads(path.read_text())
        mutate(result)
        path.write_text(json.dumps(result))

    def times(self, config, scenario, values):
        for number, value in enumerate(values, 1):
            def mutate(result):
                next(sample for sample in result["samples"] if sample["scenario"] == scenario)["milliseconds"] = value
            self.change(config, number, mutate)

    def evaluate(self):
        return gate.evaluate(self.output, self.config, "Linux", "net8.0")

    def row(self, report, scenario="single_insert_5k_tx", config="dev-os-cache"):
        return next(row for row in report["results"] if row["scenario"] == scenario and row["config"] == config)

    def test_checked_in_limits_match_owner_brief(self):
        expected = {"single_insert_5k_tx": 1.10, "bulk_insert_100k": 1.10,
                    "full_scan_3x100k": 1.10, "update_10k_one_tx": 1.10,
                    "delete_10k_one_tx": 1.10, "indexed_point_10k": 1.05}
        actual = {name: rule["maxRatio"] for name, rule in CONFIG["scenarios"].items() if rule["maxRatio"] is not None}
        self.assertEqual(expected, actual)
        self.assertEqual("dev-os-cache", CONFIG["candidate"])
        self.assertEqual(CONFIG["rounds"], RUNNER.ROUNDS)

    def test_threshold_is_inclusive_and_point_limit_is_stricter(self):
        self.times("dev-os-cache", "single_insert_5k_tx", [110] * 6)
        self.times("dev-os-cache", "indexed_point_10k", [105] * 6)
        self.assertEqual("pass", self.evaluate()["verdict"])
        self.times("dev-os-cache", "indexed_point_10k", [105.001] * 6)
        self.assertEqual("fail", self.row(self.evaluate(), "indexed_point_10k")["verdict"])

    def test_stable_small_transaction_regression_fails(self):
        self.times("dev-os-cache", "single_insert_5k_tx", [400] * 6)
        report = self.evaluate()
        self.assertEqual("fail", report["verdict"])
        self.assertEqual(4, self.row(report)["ratio"])
        self.assertFalse(report["rerunRequired"])

    def test_uses_median_not_minimum(self):
        self.config["maxSpreadPercent"] = 100
        self.times("dev-os-cache", "single_insert_5k_tx", [100, 112, 112, 112, 112, 112])
        row = self.row(self.evaluate())
        self.assertEqual("fail", row["verdict"])
        self.assertEqual(100, row["candidate"]["minMs"])
        self.assertEqual(112, row["candidate"]["medianMs"])

    def test_noise_in_either_side_requires_rerun_even_if_ratio_fails(self):
        for config in ("v5", "dev-os-cache"):
            with self.subTest(config=config):
                self.times("v5", "single_insert_5k_tx", [100] * 6)
                self.times("dev-os-cache", "single_insert_5k_tx", [400] * 6)
                self.times(config, "single_insert_5k_tx", [100, 100, 100, 100, 100, 200])
                report = self.evaluate()
                self.assertEqual("noisy", report["verdict"])
                self.assertEqual("noisy", self.row(report)["verdict"])
                self.assertTrue(report["rerunRequired"])

    def test_noise_limit_boundary(self):
        self.times("dev-os-cache", "single_insert_5k_tx", [100] * 5 + [120])
        self.assertEqual("pass", self.row(self.evaluate())["verdict"])
        self.times("dev-os-cache", "single_insert_5k_tx", [100] * 5 + [120.001])
        self.assertEqual("noisy", self.row(self.evaluate())["verdict"])

    def test_stable_failure_is_preserved_alongside_noise(self):
        self.times("dev-os-cache", "single_insert_5k_tx", [400] * 6)
        self.times("v5", "full_scan_3x100k", [100] * 5 + [200])
        report = self.evaluate()
        self.assertEqual("fail", report["verdict"])
        self.assertTrue(report["rerunRequired"])

    def test_report_only_timings_never_fail_the_gate(self):
        for scenario in CONFIG["scenarios"]:
            self.times("dev-default", scenario, [900, 100, 900, 900, 900, 900])
        for scenario in ("checkpoint_100k", "open_close_20x"):
            self.times("dev-os-cache", scenario, [900] * 6)
        self.assertEqual("pass", self.evaluate()["verdict"])
        scaling = self.row(self.evaluate(), "durable_insert_5k_4_writers", "dev-default")
        self.assertIsNone(scaling["ratio"])
        self.assertEqual("report-only", scaling["verdict"])

    def test_rejects_missing_round_and_duplicate_round(self):
        original = self.path("v5", 1).read_text()
        self.path("v5", 1).unlink()
        with self.assertRaisesRegex(ValueError, "Incomplete"):
            self.evaluate()
        self.path("v5", 1).write_text(original)
        (self.output / "round-duplicate.json").write_text(original)
        with self.assertRaisesRegex(ValueError, "Duplicate"):
            self.evaluate()

    def test_rejects_missing_duplicate_and_unknown_scenarios(self):
        for mutate in (lambda r: r["samples"].pop(), lambda r: r["samples"].append(r["samples"][0]),
                       lambda r: r["samples"][0].update(scenario="unknown")):
            original = self.path("v5", 1).read_text()
            self.change("v5", 1, mutate)
            with self.assertRaisesRegex(ValueError, "scenarios"):
                self.evaluate()
            self.path("v5", 1).write_text(original)

    def test_rejects_invalid_times(self):
        for value in (0, -1, float("nan"), float("inf"), "100", True):
            self.times("v5", "single_insert_5k_tx", [value] * 6)
            with self.assertRaisesRegex(ValueError, "Invalid time"):
                self.evaluate()

    def test_rejects_wrong_workload_binary_commit_and_runtime(self):
        mutations = [lambda r: r["samples"][0].update(operations=1),
                     lambda r: r.update(assemblySha256="c" * 64),
                     lambda r: r.update(assemblySha256=""),
                     lambda r: r.update(candidateCommit="wrong"),
                     lambda r: r.update(framework=".NETCoreApp,Version=v10.0"),
                     lambda r: r.update(runtime=".NET 10.0.0"),
                     lambda r: r.update(runnerOS="Windows"), lambda r: r.update(round=0)]
        original = self.path("dev-default", 1).read_text()
        for mutate in mutations:
            self.change("dev-default", 1, mutate)
            with self.assertRaises(ValueError):
                self.evaluate()
            self.path("dev-default", 1).write_text(original)
        with self.assertRaisesRegex(ValueError, "OS/runtime"):
            gate.evaluate(self.output, CONFIG, "unknown", "net8.0")

    def test_rejects_mismatched_v6_binaries(self):
        for number in range(1, 7):
            self.change("dev-default", number, lambda r: r.update(assemblySha256="c" * 64))
        with self.assertRaisesRegex(ValueError, "same binary"):
            self.evaluate()

    def test_each_supported_os_and_runtime_has_separate_verdicts(self):
        for operating_system in CONFIG["operatingSystems"]:
            for framework in CONFIG["frameworks"]:
                for path in self.output.glob("round-*.json"):
                    result = json.loads(path.read_text())
                    result.update(runnerOS=operating_system, framework=f".NETCoreApp,Version=v{framework[3:]}",
                                  runtime=f".NET {framework[3:]}.0")
                    path.write_text(json.dumps(result))
                report = gate.evaluate(self.output, CONFIG, operating_system, framework)
                self.assertEqual("pass", report["verdict"])
                self.assertTrue(all(row["os"] == operating_system and row["framework"] == framework
                                    for row in report["results"]))

    def test_cli_exit_codes_and_machine_readable_artifacts(self):
        command = [sys.executable, str(ROOT / ".github/scripts/benchmark_gate.py"), str(self.output),
                   "--config", str(HARNESS / "gate.json"), "--os", "Linux", "--framework", "net8.0"]
        for values, expected in (([100] * 6, 0), ([400] * 6, 1), ([100] * 5 + [200], 2)):
            self.times("dev-os-cache", "single_insert_5k_tx", values)
            child = subprocess.run(command, capture_output=True, text=True)
            self.assertEqual(expected, child.returncode, child.stderr)
            report = json.loads((self.output / "gate.json").read_text())
            self.assertEqual({0: "pass", 1: "fail", 2: "noisy"}[expected], report["verdict"])
            self.assertIn("single_insert_5k_tx", (self.output / "gate.csv").read_text())
            self.assertIn(report["verdict"].upper(), (self.output / "gate.md").read_text())

    def test_runner_summary_and_alternating_order(self):
        orders = [RUNNER.execution_order(number) for number in range(1, 7)]
        for candidate in ("dev-default", "dev-os-cache"):
            pairs = [[config for config in order if config in ("v5", candidate)] for order in orders]
            self.assertEqual([["v5", candidate], [candidate, "v5"]] * 3, pairs)
        summary_path = self.output / "job-summary.md"
        with patch.dict(os.environ, {"GITHUB_STEP_SUMMARY": str(summary_path), "RUNNER_OS": "Linux"}), \
                patch.object(sys, "argv", ["run.py", "net8.0"]), redirect_stdout(io.StringIO()):
            RUNNER.summarize(self.output)
        summary = json.loads((self.output / "summary.json").read_text())
        self.assertEqual(27, len(summary))
        for row in summary:
            self.assertEqual(100, row["medianMs"])
            self.assertEqual(100, row["minMs"])
        self.assertIn("v6 scaling only", summary_path.read_text())


if __name__ == "__main__":
    unittest.main()
