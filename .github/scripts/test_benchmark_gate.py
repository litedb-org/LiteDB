"""Synthetic timings only: these tests never run a workload or benchmark."""
import copy
import csv
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

    def row(self, report, scenario="bulk_insert_100k", config="dev-os-cache"):
        return next(row for row in report["results"] if row["scenario"] == scenario and row["config"] == config)

    def make_before(self):
        before = self.output / "before"
        before.mkdir()
        for path in self.output.glob("round-*.json"):
            result = json.loads(path.read_text())
            result["candidateCommit"] = "c" * 40
            if result["config"] != "v5":
                result["assemblySha256"] = "d" * 64
                for sample in result["samples"]:
                    sample["milliseconds"] *= 2
            (before / path.name).write_text(json.dumps(result))

    def compare(self, scenarios=""):
        return gate.evaluate_comparison(self.output, self.config, "Linux", "net8.0", "c" * 40, scenarios)

    def test_comparison_bounds_and_before_gate_do_not_change_after_gate(self):
        self.make_before()
        self.config["maxSpreadPercent"] = 100
        self.times("dev-os-cache", "bulk_insert_100k", [80, 100, 100, 100, 100, 120])
        report = self.compare()
        self.assertEqual("pass", report["verdict"])
        self.assertEqual("fail", report["baselineGate"]["verdict"])
        for row in report["comparisons"]:
            self.assertEqual("report-only", row["verdict"])
            self.assertEqual(0.5, row["ratio"])
        row = self.row(dict(results=report["comparisons"]))
        self.assertEqual(0.4, row["ratioLowerBound"])
        self.assertEqual(0.6, row["ratioUpperBound"])
        self.assertIn("Same-job after/before", gate.markdown(report))
        comparison_table = gate.markdown(report).split("### Same-job after/before")[1]
        for comparison in report["comparisons"]:
            self.assertIn(f"| {comparison['scenario']} | {comparison['config']} |", comparison_table)
        self.times("dev-os-cache", "bulk_insert_100k", [400] * 6)
        self.assertEqual("fail", self.compare()["verdict"])

    def test_comparison_rejects_missing_or_wrong_before_evidence(self):
        self.make_before()
        path = self.output / "before" / "round-1-dev-default.json"
        original = path.read_text()
        path.unlink()
        with self.assertRaisesRegex(ValueError, "Incomplete"):
            self.compare()
        result = json.loads(original)
        result["candidateCommit"] = "wrong"
        path.write_text(json.dumps(result))
        with self.assertRaises(ValueError):
            self.compare()
        for path in (self.output / "before").glob("round-*.json"):
            result = json.loads(path.read_text())
            result["candidateCommit"] = "e" * 40
            path.write_text(json.dumps(result))
        with self.assertRaisesRegex(ValueError, "workflow checkout"):
            self.compare()

    def test_comparison_rejects_different_v5_measurements(self):
        self.make_before()
        self.times("v5", "single_insert_5k_tx", [101] * 6)
        with self.assertRaisesRegex(ValueError, "identical v5"):
            self.compare()

    def test_scaling_subset_preserves_identity_and_same_job_ratios(self):
        self.make_before()
        names = {"open_close_20x", "durable_insert_5k_1_writers", "durable_insert_5k_8_writers"}
        for path in self.output.rglob("round-*.json"):
            result = json.loads(path.read_text())
            result["samples"] = [s for s in result["samples"] if s["scenario"] in names]
            path.write_text(json.dumps(result))
        report = self.compare(",".join(sorted(names)))
        self.assertEqual("report-only", report["verdict"])
        self.assertEqual(4, len(report["comparisons"]))
        self.assertTrue(all(row["ratio"] == 0.5 for row in report["comparisons"]))
        self.assertEqual(CONFIG, self.config)

    def test_subset_never_issues_gate_verdict_and_rejects_unknown_names(self):
        self.make_before()
        scenario = "single_insert_5k_tx"
        for path in self.output.rglob("round-*.json"):
            result = json.loads(path.read_text())
            result["samples"] = [s for s in result["samples"] if s["scenario"] == scenario]
            path.write_text(json.dumps(result))
        report = self.compare(scenario)
        self.assertEqual("report-only", report["verdict"])
        self.assertEqual("report-only", report["baselineGate"]["verdict"])
        self.assertTrue(all(r["maxRatio"] is None and r["verdict"] == "report-only"
                            for r in report["results"]))
        self.assertEqual(2, len(report["comparisons"]))
        self.assertIn("no Phase 1 gate verdict", gate.markdown(report))
        self.assertEqual(CONFIG, self.config)
        for invalid in ("unknown", scenario + "," + scenario, ""):
            with self.assertRaises(ValueError):
                self.compare(invalid)
        command = [sys.executable, str(ROOT / ".github/scripts/benchmark_gate.py"), str(self.output),
                   "--config", str(HARNESS / "gate.json"), "--os", "Linux", "--framework", "net8.0",
                   "--scenarios", scenario]
        self.assertEqual(1, subprocess.run(command, capture_output=True).returncode)
        child = subprocess.run(command + ["--baseline-sha", "c" * 40], capture_output=True, text=True)
        self.assertEqual(0, child.returncode, child.stderr)
        with (self.output / "gate.csv").open() as handle:
            rows = list(csv.DictReader(handle))
        self.assertEqual({"after/v5", "before/v5", "after/before"}, {r["comparison"] for r in rows})
        self.assertTrue(all(r["verdict"] == "report-only" for r in rows))

    def test_comparison_rotates_all_variants_and_preserves_both_configurations(self):
        positions = {variant: set() for variant in ("v5", "before", "after")}
        for number in range(1, 7):
            order = RUNNER.comparison_order(number)
            self.assertEqual(5, len(order))
            variants = list(dict.fromkeys(v for v, _ in order))
            for variant in variants:
                positions[variant].add(variants.index(variant))
                self.assertEqual(["v5"] if variant == "v5" else
                                 (["dev-default", "dev-os-cache"] if number % 2 else
                                  ["dev-os-cache", "dev-default"]),
                                 [c for v, c in order if v == variant])
        self.assertTrue(all(p == {0, 1, 2} for p in positions.values()))

    def test_measurement_budgets_keep_child_limit_and_absolute_caps(self):
        for operating_system in ("Windows", "Linux", "macOS"):
            with self.subTest(os=operating_system):
                self.assertEqual((360, 3300 if operating_system == "Windows" else 1380),
                                 RUNNER.measurement_budgets(True, operating_system))
                self.assertEqual((900, 3300), RUNNER.measurement_budgets(True, operating_system, True))
                self.assertEqual((360, 1440 if operating_system == "macOS" else 2100),
                                 RUNNER.measurement_budgets(False, operating_system, True))

    def test_windows_child_hang_fails_with_partial_log(self):
        destination = self.output / "timeout"
        def timeout(command, **kwargs):
            self.assertEqual(360, kwargs["timeout"])
            raise subprocess.TimeoutExpired(command, kwargs["timeout"], output=b"stalled child")
        with patch.dict(os.environ, {"GITHUB_ACTIONS": "true", "RUNNER_OS": "Windows",
                                     "BENCHMARK_BASELINE_SHA": "c" * 40,
                                     "BENCHMARK_HARNESS_SHA": "f" * 40,
                                     "BENCHMARK_SCENARIOS": "", "BENCHMARK_SLOW_COMPARISON": "false"}), \
                patch.object(sys, "argv", ["run.py", "net8.0", str(destination)]), \
                patch.object(RUNNER.subprocess, "check_output", return_value="synthetic"), \
                patch.object(RUNNER.subprocess, "run", side_effect=timeout), \
                redirect_stdout(io.StringIO()), self.assertRaises(subprocess.TimeoutExpired):
            RUNNER.main()
        metadata = json.loads((destination / "environment.json").read_text())
        self.assertEqual(3300, metadata["measurementBudgetSeconds"])
        self.assertEqual("child timeout", metadata["order"][0]["error"])
        self.assertEqual("stalled child", (destination / "round-1-v5.log").read_text())
        self.assertFalse((destination / "summary.json").exists())

    def test_windows_total_budget_exhaustion_starts_no_child(self):
        destination = self.output / "exhausted"
        with patch.dict(os.environ, {"GITHUB_ACTIONS": "true", "RUNNER_OS": "Windows",
                                     "BENCHMARK_BASELINE_SHA": "c" * 40,
                                     "BENCHMARK_HARNESS_SHA": "f" * 40,
                                     "BENCHMARK_SCENARIOS": ""}), \
                patch.object(sys, "argv", ["run.py", "net8.0", str(destination)]), \
                patch.object(RUNNER.subprocess, "check_output", return_value="synthetic"), \
                patch.object(RUNNER.subprocess, "run") as child, \
                patch.object(RUNNER.time, "monotonic", side_effect=[0, 3301]), \
                redirect_stdout(io.StringIO()), self.assertRaisesRegex(TimeoutError, "Measurement budget"):
            RUNNER.main()
        child.assert_not_called()
        self.assertFalse((destination / "summary.json").exists())

    def test_runner_comparison_uses_separate_binaries_and_revision_metadata(self):
        scenario = "single_insert_5k_tx"
        calls = []
        real_check_output = RUNNER.subprocess.check_output
        real_run = RUNNER.subprocess.run

        def child(command, **kwargs):
            if command[:1] != ["dotnet"] or len(command) != 5:
                return real_run(command, **kwargs)
            config, number, destination = command[-3:]
            env = kwargs["env"]
            calls.append((command[1], config, env["BENCHMARK_CANDIDATE_SHA"]))
            source = json.loads(self.path(config, int(number)).read_text())
            source["candidateCommit"] = env["BENCHMARK_CANDIDATE_SHA"]
            source["samples"] = [s for s in source["samples"] if s["scenario"] == scenario]
            Path(destination).write_text(json.dumps(source))
            return subprocess.CompletedProcess(command, 0, stdout="synthetic child")

        destination = self.output / "runner"
        def check_output(command, *args, **kwargs):
            if command == ["dotnet", "--info"]:
                return "synthetic dotnet info"
            return real_check_output(command, *args, **kwargs)

        with patch.dict(os.environ, {"GITHUB_ACTIONS": "true", "RUNNER_OS": "Linux",
                                     "BENCHMARK_BASELINE_SHA": "c" * 40,
                                     "BENCHMARK_HARNESS_SHA": "f" * 40,
                                     "BENCHMARK_SCENARIOS": scenario,
                                     "ImageOS": "ubuntu24", "ImageVersion": "synthetic-image"}), \
                patch.object(sys, "argv", ["run.py", "net8.0", str(destination)]), \
                patch.object(RUNNER.subprocess, "check_output", side_effect=check_output), \
                patch.object(RUNNER.subprocess, "run", side_effect=child), redirect_stdout(io.StringIO()):
            RUNNER.main()
        self.assertEqual(30, len(calls))
        self.assertEqual(12, sum("V6Before" in binary and sha == "c" * 40 for binary, _, sha in calls))
        report = gate.evaluate_comparison(destination, CONFIG, "Linux", "net8.0", "c" * 40, scenario)
        self.assertEqual("report-only", report["verdict"])
        self.assertTrue(all(r["ratio"] == 1 for r in report["comparisons"]))
        metadata = json.loads((destination / "environment.json").read_text())
        self.assertEqual(30, len(metadata["order"]))
        self.assertEqual("ubuntu24", metadata["imageName"])
        self.assertEqual("synthetic-image", metadata["imageVersion"])
        self.assertEqual(RUNNER.platform.release(), metadata["kernel"])
        summary = (self.output / "synthetic-summary.md").read_text()
        self.assertIn("ubuntu24 / synthetic-image", summary)
        self.assertIn(metadata["kernel"], summary)

    def test_image_warning_is_advisory_in_cli_and_summary(self):
        current = dict(imageName="ubuntu24", imageVersion="20261023.334.3")
        (self.output / "environment.json").write_text(json.dumps(current))
        reference = self.output / "historical-environment.json"
        command = [sys.executable, str(ROOT / ".github/scripts/benchmark_gate.py"), str(self.output),
                   "--config", str(HARNESS / "gate.json"), "--os", "Linux", "--framework", "net8.0",
                   "--compare-environment", str(reference)]
        for version, warned in (("20261023.334.3", False), ("20261024.1", True)):
            reference.write_text(json.dumps(dict(runner=dict(ImageOS="ubuntu24", ImageVersion=version))))
            for values, expected in (([100] * 6, 0), ([400] * 6, 1), ([100] * 5 + [200], 2)):
                self.times("dev-os-cache", "bulk_insert_100k", values)
                child = subprocess.run(command, capture_output=True, text=True)
                self.assertEqual(expected, child.returncode, child.stderr)
                self.assertEqual(warned, "WARNING: Comparing differing runner images" in child.stdout)
                self.assertEqual(warned, "WARNING:" in (self.output / "gate.md").read_text())
        self.assertEqual([], gate.image_warnings({}, current))
        self.assertTrue(gate.image_warnings(current, dict(imageName="ubuntu26", imageVersion=current["imageVersion"])))

    def test_single_insert_os_cache_reports_ratio_and_checkpoint_cost(self):
        scenario = "single_insert_5k_tx"
        self.times("dev-os-cache", scenario, [400] * 6)
        report = self.evaluate()
        row = self.row(report, scenario)
        self.assertEqual("pass", report["verdict"])
        self.assertEqual("report-only", row["verdict"])
        self.assertEqual(4, row["ratio"])
        self.assertIsNone(row["maxRatio"])
        self.assertIn("| single_insert_5k_tx | dev-os-cache | 4.000 |", gate.markdown(report))
        self.assertIn("report-only: cost of crash-safe checkpoints (litedb-org/LiteDB#3134)",
                      gate.markdown(report))
        self.make_before()
        comparison = self.compare()
        self.assertEqual(0.5, self.row(dict(results=comparison["comparisons"]), scenario)["ratio"])
        self.assertEqual(8, self.row(comparison["baselineGate"], scenario)["ratio"])
        self.assertEqual("report-only", self.row(report, scenario, "dev-default")["verdict"])
        # A missing measurement remains an evidence error even for report-only work.
        self.change("dev-os-cache", 1, lambda r: r["samples"].pop(0))
        with self.assertRaisesRegex(ValueError, "scenarios"):
            self.evaluate()

    def test_every_remaining_absolute_gate_retains_its_threshold(self):
        for scenario, limit in (("bulk_insert_100k", 1.10), ("full_scan_3x100k", 1.10),
                                ("update_10k_one_tx", 1.10), ("delete_10k_one_tx", 1.10),
                                ("indexed_point_10k", 1.05)):
            with self.subTest(scenario=scenario):
                self.times("dev-os-cache", scenario, [limit * 100] * 6)
                self.assertEqual("pass", self.row(self.evaluate(), scenario)["verdict"])
                self.times("dev-os-cache", scenario, [limit * 100 + 0.001] * 6)
                self.assertEqual("fail", self.row(self.evaluate(), scenario)["verdict"])
                self.times("dev-os-cache", scenario, [100] * 6)

    def test_checked_in_limits_match_owner_brief(self):
        expected = {"bulk_insert_100k": 1.10,
                    "full_scan_3x100k": 1.10, "update_10k_one_tx": 1.10,
                    "delete_10k_one_tx": 1.10, "indexed_point_10k": 1.05}
        actual = {name: rule["maxRatio"] for name, rule in CONFIG["scenarios"].items() if rule["maxRatio"] is not None}
        self.assertEqual(expected, actual)
        self.assertEqual("dev-os-cache", CONFIG["candidate"])
        self.assertEqual(CONFIG["rounds"], RUNNER.ROUNDS)

    def test_threshold_is_inclusive_and_point_limit_is_stricter(self):
        self.times("dev-os-cache", "bulk_insert_100k", [110] * 6)
        self.times("dev-os-cache", "indexed_point_10k", [105] * 6)
        self.assertEqual("pass", self.evaluate()["verdict"])
        self.times("dev-os-cache", "indexed_point_10k", [105.001] * 6)
        self.assertEqual("fail", self.row(self.evaluate(), "indexed_point_10k")["verdict"])

    def test_scan_controls_are_present_and_report_only(self):
        for scenario in ("selective_scan_3x100k", "count_scan_3x100k", "aggregate_scan_3x100k"):
            self.assertEqual({"operations": 300000, "maxRatio": None}, CONFIG["scenarios"][scenario])
            self.times("dev-os-cache", scenario, [400] * 6)
        report = self.evaluate()
        self.assertEqual("pass", report["verdict"])

    def test_stable_bulk_regression_fails(self):
        self.times("dev-os-cache", "bulk_insert_100k", [400] * 6)
        report = self.evaluate()
        self.assertEqual("fail", report["verdict"])
        self.assertEqual(4, self.row(report)["ratio"])
        self.assertFalse(report["rerunRequired"])

    def test_uses_median_not_minimum(self):
        self.config["maxSpreadPercent"] = 100
        self.times("dev-os-cache", "bulk_insert_100k", [100, 112, 112, 112, 112, 112])
        row = self.row(self.evaluate())
        self.assertEqual("fail", row["verdict"])
        self.assertEqual(100, row["candidate"]["minMs"])
        self.assertEqual(112, row["candidate"]["medianMs"])

    def test_ambiguous_noise_in_either_side_requires_rerun(self):
        for config in ("v5", "dev-os-cache"):
            with self.subTest(config=config):
                self.times("v5", "bulk_insert_100k", [100] * 6)
                self.times("dev-os-cache", "bulk_insert_100k", [112] * 6)
                self.times(config, "bulk_insert_100k", [100, 100, 100, 100, 100, 200])
                report = self.evaluate()
                self.assertEqual("noisy", report["verdict"])
                self.assertEqual("noisy", self.row(report)["verdict"])
                self.assertTrue(report["rerunRequired"])

    def test_windows_fivefold_regression_survives_noisy_ranges(self):
        for ratio in (5.25, 5.95):
            with self.subTest(ratio=ratio):
                self.times("v5", "bulk_insert_100k", [80, 100, 100, 100, 100, 150])
                self.times("dev-os-cache", "bulk_insert_100k",
                           [400, 100 * ratio, 100 * ratio, 100 * ratio, 100 * ratio, 800])
                row = self.row(self.evaluate())
                self.assertEqual("fail", row["verdict"])
                self.assertEqual(ratio, row["ratio"])
                self.assertAlmostEqual(400 / 150, row["ratioLowerBound"])
                self.assertEqual(10, row["ratioUpperBound"])
                self.assertFalse(self.evaluate()["rerunRequired"])

    def test_macos_high_spread_pass_fail_and_ambiguous(self):
        # Ten rounds, including large outliers: bounds alone decide clear cases.
        for path in self.output.glob("round-*.json"):
            result = json.loads(path.read_text())
            result["runnerOS"] = "macOS"
            path.write_text(json.dumps(result))
        for name in RUNNER.CONFIGS:
            for number in range(7, 11):
                result = json.loads(self.path(name, 1).read_text())
                result["round"] = number
                self.path(name, number).write_text(json.dumps(result))
        for values, expected in (([50] * 9 + [110], "pass"),
                                 ([111] * 9 + [200], "fail"),
                                 ([100] * 9 + [200], "noisy"),
                                 ([110] * 9 + [200], "noisy")):
            self.times("dev-os-cache", "bulk_insert_100k", values)
            report = gate.evaluate(self.output, CONFIG, "macOS", "net8.0")
            self.assertEqual(expected, self.row(report)["verdict"])
            self.assertEqual(10, report["rounds"])
        with patch.dict(os.environ, {"RUNNER_OS": "macOS"}), \
                patch.object(sys, "argv", ["run.py", "net8.0"]), redirect_stdout(io.StringIO()):
            RUNNER.summarize(self.output)
        self.assertIn("10 rounds per configuration", (self.output / "synthetic-summary.md").read_text())
        self.path("v5", 10).unlink()
        with self.assertRaisesRegex(ValueError, "Incomplete"):
            gate.evaluate(self.output, CONFIG, "macOS", "net8.0")

    def test_lower_bound_equality_is_not_failure_when_noisy(self):
        self.times("dev-os-cache", "bulk_insert_100k", [110] * 5 + [200])
        row = self.row(self.evaluate())
        self.assertEqual(1.10, row["ratioLowerBound"])
        self.assertEqual("noisy", row["verdict"])

    def test_round_config_and_ten_round_alternation(self):
        for operating_system, count in (("Linux", 6), ("Windows", 6), ("macOS", 10)):
            self.assertEqual(count, gate.rounds_for_os(CONFIG, operating_system))
            self.assertEqual(count, RUNNER.rounds_for_os(operating_system))
            for candidate in ("dev-default", "dev-os-cache"):
                pairs = [[name for name in RUNNER.execution_order(n) if name in ("v5", candidate)]
                         for n in range(1, count + 1)]
                self.assertEqual([["v5", candidate], [candidate, "v5"]] * (count // 2), pairs)
        for invalid in (True, 2, 3.5):
            config = copy.deepcopy(CONFIG)
            config["roundsByOS"]["macOS"] = invalid
            with self.assertRaises(ValueError):
                gate.validate_config(config)

    def test_noise_limit_boundary(self):
        self.times("dev-os-cache", "bulk_insert_100k", [100] * 5 + [120])
        self.assertEqual("pass", self.row(self.evaluate())["verdict"])
        self.times("dev-os-cache", "bulk_insert_100k", [100] * 5 + [120.001])
        self.assertEqual("noisy", self.row(self.evaluate())["verdict"])

    def test_stable_failure_is_preserved_alongside_noise(self):
        self.times("dev-os-cache", "bulk_insert_100k", [400] * 6)
        self.times("v5", "full_scan_3x100k", [100] * 5 + [50])
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
                config = copy.deepcopy(CONFIG)
                config["roundsByOS"] = {}  # This fixture represents six historical rounds.
                report = gate.evaluate(self.output, config, operating_system, framework)
                self.assertEqual("pass", report["verdict"])
                self.assertTrue(all(row["os"] == operating_system and row["framework"] == framework
                                    for row in report["results"]))

    def test_cli_exit_codes_and_machine_readable_artifacts(self):
        command = [sys.executable, str(ROOT / ".github/scripts/benchmark_gate.py"), str(self.output),
                   "--config", str(HARNESS / "gate.json"), "--os", "Linux", "--framework", "net8.0"]
        for values, expected in (([100] * 6, 0), ([400] * 6, 1), ([100] * 5 + [200], 2)):
            self.times("dev-os-cache", "bulk_insert_100k", values)
            child = subprocess.run(command, capture_output=True, text=True)
            self.assertEqual(expected, child.returncode, child.stderr)
            report = json.loads((self.output / "gate.json").read_text())
            self.assertEqual({0: "pass", 1: "fail", 2: "noisy"}[expected], report["verdict"])
            with (self.output / "gate.csv").open() as handle:
                csv_row = next(r for r in csv.DictReader(handle)
                           if r["scenario"] == "bulk_insert_100k" and r["config"] == "dev-os-cache")
            self.assertEqual(self.row(report)["ratioLowerBound"], float(csv_row["ratioLowerBound"]))
            self.assertEqual(self.row(report)["ratioUpperBound"], float(csv_row["ratioUpperBound"]))
            self.assertIn("Lower bound | Upper bound", (self.output / "gate.md").read_text())
            self.assertIn(report["verdict"].upper(), (self.output / "gate.md").read_text())

    def test_timed_counters_report_all_configs_and_scaling_without_changing_gate(self):
        before = self.evaluate()
        for path in self.output.glob("round-*.json"):
            result = json.loads(path.read_text())
            number = result["round"]
            for sample in result["samples"]:
                sample.update(gen0=number, gen1=number // 2, gen2=0, allocatedBytes=number * 1000)
            path.write_text(json.dumps(result))
        self.assertEqual(before, self.evaluate())
        with patch.dict(os.environ, {"RUNNER_OS": "Linux"}), \
                patch.object(sys, "argv", ["run.py", "net8.0"]), redirect_stdout(io.StringIO()):
            RUNNER.summarize(self.output)
        summary = json.loads((self.output / "summary.json").read_text())
        for row in summary:
            self.assertEqual(dict(median=3.5, min=1, max=6), row["gen0"])
            self.assertEqual(dict(median=3500, min=1000, max=6000), row["allocatedBytes"])
        self.assertIn("3500 [1000, 6000]", (self.output / "summary.md").read_text())
        with (self.output / "samples.csv").open() as handle:
            self.assertEqual("1000", next(csv.DictReader(handle))["allocatedBytes"])
        for metric, invalid in (("gen0", -1), ("gen1", True), ("gen2", None), ("allocatedBytes", 1.5)):
            original = self.path("v5", 1).read_text()
            self.change("v5", 1, lambda r: r["samples"][0].update({metric: invalid}))
            with patch.dict(os.environ, {"RUNNER_OS": "Linux"}), self.assertRaisesRegex(ValueError, metric):
                RUNNER.summarize(self.output)
            self.path("v5", 1).write_text(original)

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
        self.assertEqual(36, len(summary))
        for row in summary:
            self.assertEqual(100, row["medianMs"])
            self.assertEqual(100, row["minMs"])
            self.assertIsNone(row["allocatedBytes"])
        self.assertIn("v6 scaling only", summary_path.read_text())


if __name__ == "__main__":
    unittest.main()
