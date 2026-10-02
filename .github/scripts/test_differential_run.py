import json
import os
import shutil
import tempfile
import unittest
from pathlib import Path

import differential_run as diff
from safety_fixtures import GitRepo, run_quietly


def record(op, outcome="ok", exception=None, code=None, elapsed=1.0, dimension="mode=shared"):
    return {"target": "chaos", "step": 1, "op": op, "dimension": dimension, "outcome": outcome,
            "exceptionType": exception, "errorCode": code, "elapsedMs": elapsed}


class Runs:
    """Fixture run directories in the layout LiteDB.Fuzz writes (run.json + per-run files)."""

    def __init__(self):
        self.root = Path(tempfile.mkdtemp(prefix="differential-test-"))

    def add(self, side, outcomes, closed=(), markers=None, target="chaos", seed=1, name=None):
        directory = self.root / side / (name or f"{target}-s{seed}") / f"20260101-{target}-s{seed}-w0-rabc"
        directory.mkdir(parents=True)
        (directory / "run.json").write_text(json.dumps({"target": target, "seed": seed, "count": 3}))
        (directory / "outcomes.jsonl").write_text("".join(json.dumps(item) + "\n" for item in outcomes))
        (directory / "closed-clean.jsonl").write_text("".join(json.dumps(item) + "\n" for item in closed))
        (directory / "markers.json").write_text(json.dumps({"target": target, "seed": seed, "hits": markers or {}}))

    def compare(self, manifest=None, extra=()):
        argv = ["--base-runs", str(self.root / "base"), "--head-runs", str(self.root / "head"),
                "--out", str(self.root / "out"), *extra]
        if manifest is not None:
            path = self.root / "manifest.json"
            path.write_text(json.dumps({"changes": manifest}))
            argv += ["--manifest", str(path)]
        code, output = run_quietly(diff.main, argv)
        report = json.loads((self.root / "out" / "differential-report.json").read_text())
        return code, output, report

    def cleanup(self):
        shutil.rmtree(self.root, ignore_errors=True)


BASE = [record("Dispose"), record("Insert"), record("Insert", "threw", "LiteDB.LiteException", 132)]
INTENDED = {"call": "Dispose", "dimension": "mode=shared", "change": "new-exception", "before": "none",
            "after": "System.IO.IOException", "doc": "docs/x.md#propagate", "reason": "Close errors propagate now."}


class DifferentialRunTests(unittest.TestCase):
    def setUp(self):
        self.runs = Runs()
        self.addCleanup(self.runs.cleanup)

    def test_identical_runs_pass(self):
        closed = [{"op": "Dispose", "threads": 1, "openFds": 0, "mutexFree": True}]
        self.runs.add("base", BASE, closed, {"maintenance:close": 2})
        self.runs.add("head", BASE, closed, {"maintenance:close": 5})
        code, output, report = self.runs.compare()
        self.assertEqual(code, 0, output)
        self.assertEqual(report["status"], "passed")
        self.assertIn("No behavior difference", output)

    def test_new_escaped_exception_fails_unless_the_manifest_covers_it(self):
        self.runs.add("base", BASE)
        self.runs.add("head", BASE + [record("Dispose", "threw", "System.IO.IOException")])
        code, output, report = self.runs.compare()
        self.assertEqual(code, 1, output)
        self.assertIn("escapes System.IO.IOException", output)
        self.assertIn("**not in manifest**", output)
        code, output, report = self.runs.compare(manifest=[INTENDED])
        self.assertEqual(code, 0, output)
        self.assertIn("docs/x.md#propagate", output)

    def test_manifest_entry_for_another_exception_does_not_cover(self):
        self.runs.add("base", BASE)
        self.runs.add("head", BASE + [record("Dispose", "threw", "System.InvalidOperationException")])
        code, output, _ = self.runs.compare(manifest=[INTENDED])
        self.assertEqual(code, 1, output)

    def test_claimed_change_that_is_not_observed_fails(self):
        self.runs.add("base", BASE)
        self.runs.add("head", BASE)
        code, output, report = self.runs.compare(manifest=[INTENDED])
        self.assertEqual(code, 1, output)
        self.assertEqual(report["unusedEntries"][0]["why"], "claimed change not observed")
        code, output, report = self.runs.compare(manifest=[{**INTENDED, "call": "Rebuild"}])
        self.assertEqual(report["unusedEntries"][0]["why"], "claimed call not exercised")

    def test_removed_exception_new_outcome_kind_and_lost_operation_fail(self):
        self.runs.add("base", BASE + [record("Checkpoint")])
        self.runs.add("head", [record("Dispose", "hang"), record("Insert"), record("Insert", "threw",
                                                                                   "LiteDB.LiteException", 200)])
        code, output, report = self.runs.compare()
        self.assertEqual(code, 1, output)
        kinds = {(item["kind"], item["op"]) for item in report["differences"] if item["severity"] == "fail"}
        self.assertEqual(kinds, {("outcome-change", "Dispose"), ("new-exception", "Insert"),
                                 ("exception-removed", "Insert"), ("outcome-change", "Checkpoint")})
        self.assertIn("LiteDB.LiteException#132", output)

    def test_latency_is_advisory_unless_beyond_the_hard_factor(self):
        cases = {(1.0, 2.5): None, (4.0, 8.0): None, (10.0, 17.0): "advisory", (2.0, 9.0): "fail"}
        for (before, after), expected in cases.items():
            with self.subTest(before=before, after=after):
                runs = Runs()
                try:
                    runs.add("base", [record("Insert", elapsed=before) for _ in range(30)])
                    runs.add("head", [record("Insert", elapsed=after) for _ in range(30)])
                    code, output, report = runs.compare()
                finally:
                    runs.cleanup()
                severities = {item["severity"] for item in report["differences"] if item["kind"] == "latency"}
                self.assertEqual(severities, {expected} if expected else set(), output)
                self.assertEqual(code, 1 if expected == "fail" else 0, output)

    def test_latency_needs_enough_samples(self):
        self.runs.add("base", [record("Insert", elapsed=1.0) for _ in range(5)])
        self.runs.add("head", [record("Insert", elapsed=100.0) for _ in range(5)])
        code, output, _ = self.runs.compare()
        self.assertEqual(code, 0, output)

    def test_closed_clean_regressions_fail(self):
        self.runs.add("base", BASE, [{"op": "Dispose", "openFds": 0, "mutexFree": True, "threads": 2}])
        self.runs.add("head", BASE, [{"op": "Dispose", "openFds": 2, "mutexFree": False, "threads": 2}])
        code, output, report = self.runs.compare()
        self.assertEqual(code, 1, output)
        self.assertEqual({item["op"] for item in report["differences"]}, {"openFds", "mutexFree"})

    def test_lost_marker_fails_and_new_marker_is_advisory(self):
        self.runs.add("base", BASE, markers={"maintenance:close-during-active-op": 3, "api:A": 0})
        self.runs.add("head", BASE, markers={"api:A": 1})
        code, output, report = self.runs.compare()
        self.assertEqual(code, 1, output)
        severities = {item["op"]: item["severity"] for item in report["differences"]}
        self.assertEqual(severities, {"maintenance:close-during-active-op": "fail", "api:A": "advisory"})

    def test_collect_keeps_only_requested_runs(self):
        self.runs.add("base", BASE, seed=1)
        self.runs.add("base", [record("Corpus")], seed=99, name="corpus")
        everything = diff.collect([self.runs.root / "base"])
        requested = diff.collect([self.runs.root / "base"], {("chaos", 1)})
        self.assertIn(("Corpus", "mode=shared"), everything["ops"])
        self.assertNotIn(("Corpus", "mode=shared"), requested["ops"])
        self.assertEqual(requested["runs"], 1)

    def test_base_without_harness_is_skipped_visibly(self):
        with GitRepo() as repo:
            repo.commit({"LiteDB.Fuzz/Program.cs": "class Program { }"})
            out = self.runs.root / "out"
            code, output = run_quietly(diff.main, ["--base-tree", str(repo.path), "--head-tree", str(repo.path),
                                                   "--out", str(out)])
        self.assertEqual(code, 0, output)
        self.assertIn("Skipped:", output)
        self.assertIn("WARNING: Differential run skipped", output)
        self.assertEqual(json.loads((out / "differential-report.json").read_text())["status"], "skipped")

    def test_harness_detection_reads_fuzz_sources(self):
        root = self.runs.root / "tree"
        (root / "LiteDB.Fuzz").mkdir(parents=True)
        (root / "LiteDB.Fuzz" / "FuzzContext.cs").write_text('const string F = "trace.jsonl";')
        self.assertFalse(diff.has_harness(root))
        (root / "LiteDB.Tests" / "Safety").mkdir(parents=True)
        (root / "LiteDB.Tests" / "Safety" / "Outcomes.cs").write_text('const string F = "outcomes.jsonl";')
        self.assertTrue(diff.has_harness(root))

    def test_pr_seeds_are_fixed_and_distinct(self):
        self.assertEqual(diff.pr_seeds(3077, 3), diff.pr_seeds(3077, 3))
        self.assertEqual(len(set(diff.pr_seeds(3077, 3))), 3)
        self.assertNotEqual(diff.pr_seeds(3077, 2), diff.pr_seeds(3078, 2))


if __name__ == "__main__":
    os.environ.pop("GITHUB_STEP_SUMMARY", None)
    unittest.main()
