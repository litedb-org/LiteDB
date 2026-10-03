import json
import os
import tempfile
import unittest
from pathlib import Path

import quarantine_rerun as rerun
from safety_fixtures import GitRepo, run_quietly

LEDGER = ".github/safety/coverage-ledger.json"
TESTS = "LiteDB.Tests/Engine/Sample_Tests.cs"
SOURCE = '''namespace LiteDB.Tests.Engine
{
    public class Sample_Tests
    {
        [Fact(Skip = "Passes only as the first use: " + "the culture is snapshotted, once")]
        public void Quarantined()
        {
        }

        [Theory(DisplayName = "x, y", Skip = "flaky")]
        [InlineData(1)]
        public void Quarantined_theory(int value)
        {
        }

        [Fact]
        public void Known_finding_cleanup_skips_the_rest()
        {
        }

        [Fact]
        public void Ordinary()
        {
        }
    }
}
'''
ENTRY = {"reason": "r", "owner": "o", "gap": "g", "issue": "#1", "review": "2099-01-01", "expires": "2099-02-01"}
TRX = '''<?xml version="1.0" encoding="utf-8"?>
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Results>{results}</Results>
  <TestDefinitions>{definitions}</TestDefinitions>
</TestRun>
'''


def trx(outcomes):
    results = "".join(f'<UnitTestResult testId="{index}" testName="{name}" outcome="{outcome}" />'
                      for index, (name, outcome) in enumerate(outcomes.items()))
    definitions = "".join(
        f'<UnitTest id="{index}"><TestMethod className="{name.rsplit(".", 1)[0]}" name="{name.rsplit(".", 1)[1]}" />'
        '</UnitTest>' for index, name in enumerate(outcomes))
    return TRX.format(results=results, definitions=definitions)


class QuarantineRerunTests(unittest.TestCase):
    def setUp(self):
        self.scratch = Path(tempfile.mkdtemp(prefix="quarantine-"))

    def plan(self, unskip=False):
        quarantine = [{"test": "LiteDB.Tests.Engine.Sample_Tests.Quarantined", **ENTRY},
                      {"test": "LiteDB.Tests.Engine.Sample_Tests.Quarantined_theory", **ENTRY}]
        with GitRepo() as repo:
            repo.commit({TESTS: b"\xef\xbb\xbf" + SOURCE.replace("\n", "\r\n").encode(),
                         LEDGER: json.dumps({"quarantine": quarantine})})
            output = self.scratch / "plan.json"
            code, text = run_quietly(rerun.main, ["plan", "--output", str(output)] + (["--unskip"] if unskip else []))
            raw = (repo.path / TESTS).read_bytes()
            self.assertTrue(raw.startswith(b"\xef\xbb\xbf"), "the BOM is kept")
            self.assertEqual(raw.count(b"\n"), raw.count(b"\r\n"), "CRLF line endings are kept")
            source = raw.decode("utf-8-sig").replace("\r\n", "\n")
        self.assertEqual(code, 0, text)
        return json.loads(output.read_text()), source

    def test_plan_lists_quarantined_and_known_finding_tests(self):
        plan, source = self.plan()
        self.assertEqual([(item["test"].rsplit(".", 1)[1], item["kind"], item["skipped"]) for item in plan["entries"]],
                         [("Quarantined", "quarantine", True), ("Quarantined_theory", "quarantine", True),
                          ("Known_finding_cleanup_skips_the_rest", "known-finding", False)])
        self.assertEqual(plan["projects"], ["LiteDB.Tests"])
        self.assertIn("FullyQualifiedName=LiteDB.Tests.Engine.Sample_Tests.Quarantined|", plan["filter"])
        self.assertEqual(source, SOURCE)

    def test_unskip_removes_only_the_skip_argument(self):
        _, source = self.plan(unskip=True)
        self.assertIn("        [Fact]\n        public void Quarantined()", source)
        self.assertIn('[Theory(DisplayName = "x, y")]\n        [InlineData(1)]', source)
        self.assertNotIn("Skip", source)
        self.assertEqual(source.count("[Fact]"), 3)

    def report(self, outcomes, entries, today="2026-10-05"):
        plan = self.scratch / "plan.json"
        plan.write_text(json.dumps({"entries": entries}))
        results = self.scratch / "results"
        results.mkdir(exist_ok=True)
        (results / "run.trx").write_text(trx(outcomes))
        return run_quietly(rerun.main, ["report", "--plan", str(plan), "--results", str(results), "--today", today])

    def test_report_flags_only_what_needs_attention(self):
        quarantined = {"test": "A.T.Still_broken", "kind": "quarantine", "path": "x", **ENTRY}
        known = {"test": "A.T.Known_finding_x", "kind": "known-finding", "path": "x"}
        code, output = self.report({"A.T.Still_broken": "Failed", "A.T.Known_finding_x": "Passed"},
                                   [quarantined, known])
        self.assertEqual(code, 0, output)
        self.assertIn("| `A.T.Still_broken` | quarantine | failed | - |", output)

    def test_report_attention_cases(self):
        entries = [{"test": "A.T.Now_passes", "kind": "quarantine", "path": "x", **ENTRY},
                   {"test": "A.T.Known_finding_fixed", "kind": "known-finding", "path": "x"},
                   {"test": "A.T.Missing", "kind": "known-finding", "path": "x"},
                   {"test": "A.T.Expired", "kind": "quarantine", "path": "x",
                    **{**ENTRY, "review": "2026-01-01", "expires": "2026-02-01"}},
                   {"test": "A.T.Review_due", "kind": "quarantine", "path": "x", **{**ENTRY, "review": "2026-10-01"}}]
        code, output = self.report({"A.T.Now_passes": "Passed", "A.T.Known_finding_fixed": "Failed",
                                    "A.T.Expired": "Failed", "A.T.Review_due": "Failed"}, entries)
        self.assertEqual(code, 1)
        for expected in ("restore it to a CI leg", "no longer reproduces as pinned", "no result",
                         "quarantine expired (2026-02-01", "review date passed (2026-10-01"):
            self.assertIn(expected, output)

    def test_the_repository_plan_covers_the_ledger(self):
        code, output = run_quietly(rerun.main, ["plan", "--output", os.devnull])
        self.assertEqual(code, 0, output)


if __name__ == "__main__":
    unittest.main()
