import json
import os
import unittest

import differential_run as diff
from safety_fixtures import run_quietly
from test_differential_run import BASE, Runs

CLAIM = {"call": "LiteDatabase.Dispose", "change": "cleanup-change", "before": "Discarded", "after": "Propagated",
         "doc": "docs/x.md#propagate", "reason": "Close errors propagate now."}


def reached(step, site):
    return {"target": "teardown-faults", "step": step, "fault": site, "op": None, "fired": True,
            "declared": None, "observed": None}


def disposed(step, path, observed, declared="Propagated, Discarded"):
    return {"target": "teardown-faults", "step": step, "fault": None, "op": path, "fired": True,
            "declared": declared, "observed": observed}


def case(step, path, site, model, observed):
    return {"target": "teardown-faults", "step": step, "driver": f"{path}/variant/direct", "site": site,
            "model": model, "fired": True, "observed": observed}


def teardown(path, cases):
    """faults.jsonl and teardown.jsonl rows as TeardownFaultsFuzzer writes them: (site, model, observed) per step."""
    faults, rows = [], []
    for step, (site, model, observed) in enumerate(cases, start=1):
        faults += [reached(step, site), disposed(step, path, observed)]
        rows += [{**case(step, path, None, "baseline", "None"), "fired": False}, case(step, path, site, model, observed)]
    return faults, rows


class ExerciseTests(unittest.TestCase):
    def setUp(self):
        self.runs = Runs()
        self.addCleanup(self.runs.cleanup)

    def add(self, side, cases, path="LiteDatabase.Dispose", name=None):
        self.runs.add(side, BASE, target="teardown-faults", name=name)
        run = sorted((self.runs.root / side / (name or "teardown-faults-s1")).rglob("run.json"))[0].parent
        faults, rows = teardown(path, cases)
        (run / "faults.jsonl").write_text("".join(json.dumps(item) + "\n" for item in faults))
        (run / "teardown.jsonl").write_text("".join(json.dumps(item) + "\n" for item in rows))

    def test_a_new_disposition_at_a_site_both_trees_exercised_fails_even_if_another_site_showed_it(self):
        # The base propagates at the disk site and discards at the checkpoint site; the head now propagates at the
        # checkpoint site. The path's pair SET is the same on both sides, so only the per-site key shows the change.
        self.add("base", [("LiteEngine.Close.disk", "skip", "Propagated"),
                          ("LiteEngine.Close.checkpoint", "skip", "Discarded")])
        self.add("head", [("LiteEngine.Close.disk", "skip", "Propagated"),
                          ("LiteEngine.Close.checkpoint", "skip", "Propagated")])
        code, output, report = self.runs.compare()
        self.assertEqual(code, 1, output)
        found = [item for item in report["differences"] if item["kind"] == "cleanup-change"]
        self.assertEqual([(item["op"], item["dimension"]) for item in found],
                         [("LiteDatabase.Dispose", "site=LiteEngine.Close.checkpoint;model=skip")])
        self.assertIn("at this site (base here: Propagated, Discarded -> Discarded)", found[0]["detail"])
        code, output, report = self.runs.compare(manifest=[{**CLAIM, "dimension": "site=LiteEngine.Close.checkpoint;*"}])
        self.assertEqual(code, 0, output)
        self.assertEqual(report["manifestStatus"][0]["state"], "changed")

    def test_fail_inside_and_skip_at_one_site_are_different_keys(self):
        self.add("base", [("LiteEngine.Close.disk", "fail-inside", "Discarded"),
                          ("LiteEngine.Close.disk", "skip", "Discarded")])
        self.add("head", [("LiteEngine.Close.disk", "fail-inside", "Discarded"),
                          ("LiteEngine.Close.disk", "skip", "Propagated")])
        code, output, report = self.runs.compare()
        self.assertEqual(code, 1, output)
        self.assertEqual([item["dimension"] for item in report["differences"]],
                         ["site=LiteEngine.Close.disk;model=skip"])

    def test_exercise_counts_distinguish_unchanged_from_not_exercised(self):
        self.add("base", [("LiteEngine.Close.checkpoint", "fail-inside", "Discarded"),
                          ("LiteEngine.Close.monitor", "skip", "Discarded")])
        self.add("head", [("LiteEngine.Close.checkpoint", "fail-inside", "Discarded"),
                          ("LiteEngine.Close.checkpoint", "fail-inside", "Discarded"),
                          ("SortDisk.Dispose.pool", "skip", "Discarded")])
        code, output, report = self.runs.compare()
        # The monitor site's fault point is no longer reached: a lost fault point, not a disposition change.
        self.assertEqual([(item["kind"], item["op"]) for item in report["differences"]],
                         [("marker", "fault-point:LiteEngine.Close.monitor")])
        rows = {(row["site"], row["model"]): (row["base"], row["head"], row["state"])
                for row in report["exercise"]["dispositions"]}
        self.assertEqual(rows, {("LiteEngine.Close.checkpoint", "fail-inside"): (1, 2, "unchanged"),
                                ("LiteEngine.Close.monitor", "skip"): (1, 0, "not-exercised-head"),
                                ("SortDisk.Dispose.pool", "skip"): (0, 1, "not-exercised-base")})
        self.assertIn("FaultDisposed (path, site, model): 0 exercised and changed, 1 exercised and unchanged, "
                      "1 not exercised on the head, 1 not exercised on the base.", output)

    def test_unobserved_cleanup_claim_on_an_exercised_path_reads_exercised_unchanged(self):
        self.add("base", [("LiteEngine.Close.checkpoint", "fail-inside", "Discarded")])
        self.add("head", [("LiteEngine.Close.checkpoint", "fail-inside", "Discarded")])
        code, output, report = self.runs.compare(manifest=[CLAIM])
        self.assertEqual(code, 1, output)
        unused = report["unusedEntries"][0]
        self.assertEqual(unused["state"], "unchanged")
        self.assertEqual(unused["why"], "claimed change not observed: exercised, unchanged (base 1, head 1)")
        self.assertEqual(unused["exercised"], {"base": 1, "head": 1})
        self.assertIn("**claimed change not observed: exercised, unchanged (base 1, head 1)**", output)

    def test_cleanup_claim_states_name_the_side_that_did_not_exercise_the_path(self):
        self.add("base", [("LiteEngine.Close.checkpoint", "fail-inside", "Discarded")])
        self.add("head", [("LiteEngine.Close.checkpoint", "fail-inside", "Discarded")], path="SharedEngine.Dispose")
        code, output, report = self.runs.compare(manifest=[CLAIM, {**CLAIM, "call": "SharedEngine.Dispose"},
                                                           {**CLAIM, "call": "LiteEngine.Dispose"}])
        self.assertEqual([(item["call"], item["state"], item["why"]) for item in report["unusedEntries"]], [
            ("LiteDatabase.Dispose", "not-exercised-head", "claimed call not exercised on the head (base 1, head 0)"),
            ("SharedEngine.Dispose", "not-exercised-base", "claimed call not exercised on the base (base 0, head 1)"),
            ("LiteEngine.Dispose", "not-exercised", "claimed call not exercised on either side")])

    def test_a_claim_exercised_on_both_sides_at_different_sites_only_says_so(self):
        self.add("base", [("LiteEngine.Close.checkpoint", "fail-inside", "Discarded")])
        self.add("head", [("LiteEngine.Close.disk", "skip", "Discarded")])
        code, output, report = self.runs.compare(manifest=[CLAIM])
        self.assertEqual(report["unusedEntries"][0]["why"], "claimed change not observed: exercised, unchanged "
                         "(base 1, head 1; never at the same site or evaluation key on both sides)")

    def test_a_site_only_the_head_exercised_falls_back_to_the_path_level_pairs(self):
        self.add("base", [("LiteEngine.Close.checkpoint", "fail-inside", "Discarded")])
        self.add("head", [("LiteEngine.Close.checkpoint", "fail-inside", "Discarded"),
                          ("LiteEngine.Close.disk", "skip", "Propagated")])
        code, output, report = self.runs.compare()
        self.assertEqual(code, 1, output)
        self.assertEqual([(item["dimension"], item["detail"]) for item in report["differences"]], [
            ("site=LiteEngine.Close.disk;model=skip", "FaultDisposed: declared -> observed Propagated, Discarded -> "
             "Propagated new on the head on this path; the base never exercised this site")])

    def test_a_site_whose_disposition_varies_between_repeats_is_schedule_dependent(self):
        for side, observed in (("base", ("Discarded", "Discarded")), ("head", ("Propagated", "Discarded"))):
            for name, value in zip(("r0", "r1"), observed):
                self.add(side, [("SharedEngine.Dispose.readers", "skip", value)], name=name)
        root, out = self.runs.root, ["--out", str(self.runs.root / "out"), "--blocking"]
        argv = ["--base-runs", str(root / "base" / "r0"), str(root / "base" / "r1"),
                "--head-runs", str(root / "head" / "r0"), str(root / "head" / "r1")]
        code, output = run_quietly(diff.main, argv + out)
        self.assertEqual(code, 0, output)
        self.assertIn("`LiteDatabase.Dispose [site=SharedEngine.Dispose.readers;model=skip]`", output)
        argv[-1] = str(root / "head" / "r0")  # one head schedule only: no evidence of variation, so it fails
        code, output = run_quietly(diff.main, argv + out)
        self.assertEqual(code, 1, output)

if __name__ == "__main__":
    os.environ.pop("GITHUB_STEP_SUMMARY", None)
    unittest.main()
