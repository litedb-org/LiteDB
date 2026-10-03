import json
import os
import unittest

import differential_run as diff
from safety_fixtures import run_quietly
from test_differential_run import Runs, record

COUNT = 5


class EarlyStopTests(unittest.TestCase):
    """A fuzz run stops at its first failure; operation classes it never reached are not compared individually."""

    def setUp(self):
        self.runs = Runs()
        self.addCleanup(self.runs.cleanup)

    def add(self, side, steps, stop=None, name="r0", seed=1):
        """steps: {op: first step}; stop: the step the run failed in (None = completed COUNT steps)."""
        outcomes = [record(op, step=step) for op, step in steps.items()]
        self.runs.add(side, outcomes, seed=seed, name=name)
        run = sorted((self.runs.root / side / name).rglob("run.json"))[-1]
        meta = json.loads(run.read_text())
        meta.update(status="failed" if stop else "passed", steps=stop or COUNT, count=COUNT,
                    failureId="CHAOS_SOMETHING" if stop else None)
        run.write_text(json.dumps(meta))

    def judge(self, repeats=("r0",), manifest=None):
        argv = ["--base-runs", *(str(self.runs.root / "base" / name) for name in repeats),
                "--head-runs", *(str(self.runs.root / "head" / name) for name in repeats),
                "--out", str(self.runs.root / "out"), "--blocking"]
        if manifest is not None:
            (self.runs.root / "manifest.json").write_text(json.dumps({"changes": manifest}))
            argv += ["--manifest", str(self.runs.root / "manifest.json")]
        code, output = run_quietly(diff.main, argv)
        return code, output, json.loads((self.runs.root / "out" / "differential-report.json").read_text())

    def test_classes_the_base_saw_only_after_the_head_stopped_are_one_note_not_findings(self):
        self.add("base", {"Insert": 1, "Rebuild": 3, "Checkpoint": 4}, stop=5)
        self.add("head", {"Insert": 1}, stop=2)
        code, output, report = self.judge()
        self.assertEqual(code, 0, output)
        self.assertEqual(report["differences"], [])
        note, = report["exercise"]["stopNotes"]
        self.assertEqual({key: note[key] for key in ("target", "seed", "repeat", "stopStep", "failureId", "keys",
                                                     "baseStop", "failing")},
                         {"target": "chaos", "seed": 1, "repeat": 0, "stopStep": 2, "failureId": "CHAOS_SOMETHING",
                          "keys": 2, "baseStop": 5, "failing": False})
        self.assertIn("- `chaos` seed 1 r0: the head run stopped at step 2 (CHAOS_SOMETHING); the base run stopped at "
                      "step 5. 2 operation class(es) the base exercised only at steps >= 2 are not compared, e.g. "
                      "`Checkpoint [mode=shared]`, `Rebuild [mode=shared]`.", output)
        self.assertIn("2 not exercised on the head (2 of them only after a head run stopped early)", output)

    def test_a_class_the_base_saw_at_a_step_the_head_reached_is_still_a_finding(self):
        self.add("base", {"Insert": 1, "Delete": 1, "Rebuild": 2}, stop=5)
        self.add("head", {"Insert": 1}, stop=2)  # step 2 failed on the head, so Rebuild at step 2 is explained
        code, output, report = self.judge()
        self.assertEqual(code, 1, output)
        self.assertEqual([(item["kind"], item["op"]) for item in report["differences"]], [("outcome-change", "Delete")])
        self.assertEqual(report["exercise"]["stopNotes"][0]["keys"], 1)

    def test_a_head_run_that_fails_where_the_base_completed_fails_once(self):
        self.add("base", {"Insert": 1, "Rebuild": 3, "Checkpoint": 4})
        self.add("head", {"Insert": 1}, stop=2)
        code, output, report = self.judge(manifest=[{"call": "chaos", "change": "outcome-change", "before": "a",
                                                     "after": "b", "doc": "docs/x.md#y", "reason": "not coverable"}])
        self.assertEqual(code, 1, output)
        found, = report["differences"]
        self.assertEqual((found["kind"], found["op"], found["dimension"], found.get("intended")),
                         ("run-stopped-early", "chaos", "seed=1;repeat=0", None))
        self.assertIn("**Fails**: the base run completed.", output)
        self.assertEqual(report["wouldFail"], 2)  # the note and the unused entry; not one finding per class

    def test_every_base_run_with_the_class_must_be_explained(self):
        self.add("base", {"Insert": 1, "Rebuild": 3}, stop=5, name="r0")
        self.add("base", {"Insert": 1, "Rebuild": 1}, stop=5, name="r1")
        self.add("head", {"Insert": 1}, stop=2, name="r0")
        self.add("head", {"Insert": 1}, stop=4, name="r1")  # r1 reached step 1 and never saw Rebuild
        code, output, report = self.judge(repeats=("r0", "r1"))
        self.assertEqual(code, 1, output)
        self.assertEqual([(item["kind"], item["op"]) for item in report["differences"]], [("outcome-change", "Rebuild")])
        self.assertEqual(report["exercise"]["stopNotes"], [])

    def test_runs_pair_up_by_target_seed_and_repeat(self):
        self.add("base", {"Insert": 1, "Rebuild": 3}, stop=5, seed=2)
        self.add("head", {"Insert": 1}, stop=2, seed=1)  # another seed's stop explains nothing
        self.add("head", {"Insert": 1}, seed=2, name="r0b")
        argv = ["--base-runs", str(self.runs.root / "base" / "r0"), "--head-runs", str(self.runs.root / "head"),
                "--out", str(self.runs.root / "out"), "--blocking"]
        code, output = run_quietly(diff.main, argv)
        self.assertEqual(code, 1, output)
        self.assertIn("operation class no longer exercised on the head", output)

    def test_records_without_a_step_are_never_explained(self):
        self.add("base", {"Insert": 1, "Rebuild": None}, stop=5)
        self.add("head", {"Insert": 1}, stop=2)
        code, output, report = self.judge()
        self.assertEqual(code, 1, output)
        self.assertEqual([item["op"] for item in report["differences"]], ["Rebuild"])

    def test_run_json_without_a_status_is_not_an_early_stop(self):
        self.add("base", {"Insert": 1, "Rebuild": 3}, stop=5)
        self.add("head", {"Insert": 1}, stop=2)
        run = next((self.runs.root / "head").rglob("run.json"))
        run.write_text(json.dumps({"target": "chaos", "seed": 1, "count": COUNT}))
        code, output, report = self.judge()
        self.assertEqual(code, 1, output)
        self.assertEqual([item["op"] for item in report["differences"]], ["Rebuild"])

    def test_an_unused_claim_on_a_class_cut_off_by_the_stop_says_so(self):
        self.add("base", {"Insert": 1, "Rebuild": 3}, stop=5)
        self.add("head", {"Insert": 1}, stop=2)
        claim = {"call": "Rebuild", "change": "new-exception", "before": "none", "after": "System.IO.IOException",
                 "doc": "docs/x.md#y", "reason": "Rebuild failures propagate now."}
        code, output, report = self.judge(manifest=[claim])
        self.assertEqual(code, 1, output)
        self.assertEqual(report["unusedEntries"][0]["why"], "claimed call not exercised on the head (base 1, head 0); "
                         "the head runs stopped before the steps where the base exercised it")


if __name__ == "__main__":
    os.environ.pop("GITHUB_STEP_SUMMARY", None)
    unittest.main()
