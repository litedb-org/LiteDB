import json
import os
import shutil
import tempfile
import unittest
from pathlib import Path

import differential_run as diff
from safety_fixtures import GitRepo, run_quietly


def record(op, outcome="ok", exception=None, code=None, elapsed=1.0, dimension="mode=shared", **extra):
    return {"target": "chaos", "step": 1, "op": op, "dimension": dimension, "outcome": outcome,
            "exceptionType": exception, "errorCode": code, "elapsedMs": elapsed, **extra}


class Runs:
    """Fixture run directories in the layout LiteDB.Fuzz writes (run.json + per-run files)."""

    def __init__(self):
        self.root = Path(tempfile.mkdtemp(prefix="differential-test-"))

    def add(self, side, outcomes, closed=(), markers=None, target="chaos", seed=1, name=None,
            obligations="connection-clean.jsonl"):
        directory = self.root / side / (name or f"{target}-s{seed}") / f"20260101-{target}-s{seed}-w0-rabc"
        directory.mkdir(parents=True)
        (directory / "run.json").write_text(json.dumps({"target": target, "seed": seed, "count": 3}))
        (directory / "outcomes.jsonl").write_text("".join(json.dumps(item) + "\n" for item in outcomes))
        (directory / obligations).write_text("".join(json.dumps(item) + "\n" for item in closed))
        (directory / "markers.json").write_text(json.dumps({"target": target, "seed": seed, "hits": markers or {}}))

    def compare(self, manifest=None, extra=()):
        argv = ["--base-runs", str(self.root / "base"), "--head-runs", str(self.root / "head"),
                "--out", str(self.root / "out"), *(extra or ["--blocking"])]
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
CLEAN = {"op": "Dispose", "clean": True, "violations": [], "threads": 1, "openFds": 0}


def failing(report):
    return {(item["kind"], item["op"]) for item in report["differences"] if not item.get("intended")}


class DifferentialRunTests(unittest.TestCase):
    def setUp(self):
        self.runs = Runs()
        self.addCleanup(self.runs.cleanup)

    def test_identical_normalized_outcomes_pass_despite_timing_and_metric_noise(self):
        self.runs.add("base", BASE, [CLEAN], {"maintenance:close": 2})
        head = [dict(item, elapsedMs=item["elapsedMs"] * 50, step=9) for item in reversed(BASE)]
        self.runs.add("head", head, [dict(CLEAN, threads=4, openFds=3)], {"maintenance:close": 5})
        code, output, report = self.runs.compare()
        self.assertEqual(code, 0, output)
        self.assertEqual(report["status"], "passed")
        self.assertIn("No normalized behavior difference", output)

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
        self.assertEqual(failing(report), {("outcome-change", "Dispose"), ("new-exception", "Insert"),
                                           ("exception-removed", "Insert"), ("outcome-change", "Checkpoint")})
        self.assertIn("LiteDB.LiteException#132", output)

    def test_latency_is_not_compared(self):
        self.runs.add("base", [record("Insert", elapsed=1.0) for _ in range(30)])
        self.runs.add("head", [record("Insert", elapsed=500.0) for _ in range(30)])
        code, output, report = self.runs.compare()
        self.assertEqual(code, 0, output)
        self.assertEqual(report["differences"], [])

    def test_unpermitted_outcome_fails_and_is_not_coverable(self):
        self.runs.add("base", [record("Read", permitted=["ok", "refused"])])
        self.runs.add("head", [record("Read", permitted=["ok", "refused"]),
                               record("Read", "threw", "System.ObjectDisposedException", permitted=["ok", "refused"])])
        manifest = [{**INTENDED, "call": "Read", "after": "System.ObjectDisposedException"}]
        code, output, report = self.runs.compare(manifest=manifest)
        self.assertEqual(code, 1, output)
        self.assertIn(("outcome-not-permitted", "Read"), failing(report))
        self.assertIn("**not coverable**", output)

    def test_differences_within_a_declared_permitted_set_are_permitted_variation(self):
        allowed = ["ok", "threw:LiteDB.LiteException#137"]
        self.runs.add("base", [record("Upsert", permitted=allowed)])
        self.runs.add("head", [record("Upsert", permitted=allowed),
                               record("Upsert", "threw", "LiteDB.LiteException", 137, permitted=allowed)])
        code, output, report = self.runs.compare()
        self.assertEqual(code, 0, output)
        self.assertEqual({item.get("class") for item in report["differences"]}, {"permitted-variation"})
        runs = Runs()
        self.addCleanup(runs.cleanup)
        runs.add("base", [record("Upsert", permitted=allowed)])
        runs.add("head", [record("Upsert", permitted=allowed),
                          record("Upsert", "threw", "LiteDB.LiteException", 132, permitted=allowed)])
        code, output, report = runs.compare()
        self.assertEqual(code, 1, output)
        self.assertIn(("outcome-not-permitted", "Upsert"), failing(report))

    def test_primary_failure_replaced_by_cleanup_failure_fails(self):
        preserved = record("Commit", "threw", "System.IO.IOException", primaryExceptionType="System.IO.IOException")
        replaced = record("Commit", "threw", "LiteDB.LiteException", 9, primaryExceptionType="System.IO.IOException")
        self.runs.add("base", [preserved, replaced])
        self.runs.add("head", [preserved, replaced])
        self.assertEqual(self.runs.compare()[0], 0)
        runs = Runs()
        self.addCleanup(runs.cleanup)
        runs.add("base", [preserved])
        runs.add("head", [replaced])
        code, output, report = runs.compare()
        self.assertEqual(code, 1, output)
        self.assertIn(("primary-changed", "Commit"), failing(report))
        self.assertIn("System.IO.IOException -> LiteDB.LiteException#9", output)

    def test_payload_and_effect_digests_are_compared_as_multisets(self):
        base = [record("Find", payloadDigest="a"), record("Find", payloadDigest="b"),
                record("Insert", effectsDigest="x")]
        self.runs.add("base", base)
        self.runs.add("head", [record("Find", payloadDigest="b"), record("Find", payloadDigest="a"),
                               record("Insert", effectsDigest="y")])
        code, output, report = self.runs.compare()
        self.assertEqual(code, 1, output)
        self.assertEqual(failing(report), {("effect-change", "Insert")})

    def test_absent_optional_fields_are_reported_not_compared(self):
        self.runs.add("base", [record("Find", payloadDigest="a")])
        self.runs.add("head", [record("Find")])
        code, output, report = self.runs.compare()
        self.assertEqual(code, 0, output)
        self.assertIn("Find [mode=shared]: payloads recorded for 1/1 base and 0/1 head calls", output)
        self.assertEqual(report["notCompared"]["absent"], {"effects": 1, "primary": 1})
        self.assertIn("effects for 1 operation class(es)", output)

    def test_cleanup_obligations_compare_clean_results_and_violation_kinds(self):
        for name in ("connection-clean.jsonl", "quiescent.jsonl", "closed-clean.jsonl"):
            with self.subTest(name):
                runs = Runs()
                try:
                    runs.add("base", BASE, [CLEAN], obligations=name)
                    runs.add("head", BASE, [dict(CLEAN, clean=False, violations=["handles: -log open"]),
                                            dict(CLEAN, violations=[{"kind": "threads"}])], obligations=name)
                    code, output, report = runs.compare()
                finally:
                    runs.cleanup()
                self.assertEqual(code, 1, output)
                details = [item["detail"] for item in report["differences"]]
                self.assertEqual(len(details), 3, details)
                self.assertTrue(any("new violation 'handles'" in item for item in details), details)

    def test_fault_dispositions_and_reached_fault_points_are_compared(self):
        self.runs.add("base", BASE)
        self.runs.add("head", BASE)
        rows = {"base": [{"fault": "pin-close", "fired": True}, {"fault": "commit", "fired": True},
                         {"fault": None, "op": "Dispose", "declared": "discard", "observed": "discard"}],
                "head": [{"fault": "pin-close", "fired": True}, {"fault": "new-hook", "fired": True},
                         {"fault": None, "op": "Dispose", "declared": "discard", "observed": "propagated"}]}
        for side, items in rows.items():
            run = next((self.runs.root / side).rglob("run.json")).parent
            (run / "faults.jsonl").write_text("".join(json.dumps(item) + "\n" for item in items))
        code, output, report = self.runs.compare()
        self.assertEqual(code, 1, output)
        self.assertEqual(failing(report), {("cleanup-change", "Dispose"), ("marker", "fault-point:commit")})
        self.assertEqual(report["capabilities"]["faults"], ["new-hook"])

    def test_quiescent_records_key_by_point(self):
        self.runs.add("base", BASE, [{"point": "scenario end", "clean": True, "violations": []}],
                      obligations="quiescent.jsonl")
        self.runs.add("head", BASE, [{"point": "scenario end", "clean": False, "violations": ["scratch: -tmp left"]}],
                      obligations="quiescent.jsonl")
        code, output, report = self.runs.compare()
        self.assertEqual(code, 1, output)
        self.assertEqual(failing(report), {("cleanup-change", "scenario end")})

    def test_head_only_operations_and_markers_are_capabilities(self):
        self.runs.add("base", BASE, markers={"maintenance:close-during-active-op": 3, "api:A": 0})
        self.runs.add("head", BASE + [record("BeginTransaction")], markers={"api:A": 1})
        code, output, report = self.runs.compare()
        self.assertEqual(code, 1, output)
        self.assertEqual(failing(report), {("marker", "maintenance:close-during-active-op")})
        self.assertEqual(report["capabilities"], {"operations": ["BeginTransaction [mode=shared]"],
                                                  "markers": ["api:A"], "faults": []})
        self.assertIn("`BeginTransaction [mode=shared]`", output)

    def test_advisory_mode_reports_and_exits_zero(self):
        self.runs.add("base", BASE)
        self.runs.add("head", BASE + [record("Dispose", "threw", "System.IO.IOException")])
        code, output, report = self.runs.compare(extra=["--advisory"])
        self.assertEqual(code, 0, output)
        self.assertEqual(report["status"], "advisory")
        self.assertIn("would fail the run", output)
        self.assertIn("2 unexplained difference(s) (advisory)", output)

    def test_operations_varying_between_repeats_of_one_tree_are_schedule_dependent(self):
        racing = [record("Upsert"), record("Upsert", "threw", "LiteDB.LiteException", 137)]
        self.runs.add("base", racing, name="r0")
        self.runs.add("base", [record("Upsert")], name="r1")
        self.runs.add("head", [record("Upsert")], name="r0")
        self.runs.add("head", [record("Upsert")], name="r1")
        argv = ["--base-runs", str(self.runs.root / "base" / "r0"), str(self.runs.root / "base" / "r1"),
                "--head-runs", str(self.runs.root / "head" / "r0"), str(self.runs.root / "head" / "r1"),
                "--out", str(self.runs.root / "out"), "--blocking"]
        code, output = run_quietly(diff.main, argv)
        self.assertEqual(code, 0, output)
        self.assertIn("schedule-dependent (varies between repeats", output)
        self.assertIn("`Upsert [mode=shared]`", output)
        argv[argv.index(str(self.runs.root / "base" / "r1"))] = str(self.runs.root / "base" / "r0")
        code, output = run_quietly(diff.main, argv)
        self.assertEqual(code, 1, output)

    def test_runs_without_outcome_records_fail(self):
        self.runs.add("base", BASE)
        self.runs.add("head", BASE)
        (next((self.runs.root / "head").rglob("outcomes.jsonl"))).unlink()
        code, output, report = self.runs.compare()
        self.assertEqual(code, 1, output)
        self.assertIn("no head run wrote outcomes.jsonl", output)

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
