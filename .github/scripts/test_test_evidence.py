import json
import os
import shutil
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

import check_test_evidence as check
from safety_fixtures import GitRepo, run_quietly, csharp_class

SHA = "a" * 40
GUARD = check.RUNTIME_GUARD
CONFIG = {"jobs": {
    "build-linux": {"tiers": ["pr", "full"]},
    "test-linux": {"tiers": ["pr", "full"], "legs": {"pr": {"item.runtimeMajor": [8, 10]}}},
    "test-macos": {"tiers": ["full"], "legs": {"full": {"item.runtimeMajor": [8]}}},
}}
NEEDS = {"build-linux": {"result": "success"}, "test-linux": {"result": "success"}, "test-macos": {"result": "skipped"}}
SOURCE = {
    "LiteDB.Tests/Engine/TestHost_Tests.cs": csharp_class("TestHost_Tests", {
        "RequestedRuntimeAndArchitecture_AreActuallyRunning": ("Fact", "")}),
    "LiteDB.Tests/Engine/Sample_Tests.cs": csharp_class("Sample_Tests", {
        "Runs": ("Fact", ""), "Skipped": ("Fact(Skip = \"x\")", "")}),
}
RUNS = "LiteDB.Tests.Engine.Sample_Tests.Runs"
SKIPPED = "LiteDB.Tests.Engine.Sample_Tests.Skipped"
QUARANTINE = [{"test": SKIPPED, "reason": "r", "owner": "o", "review": "2099-01-01", "gap": "g"}]


def trx(results, aborted=False):
    definitions = "".join(
        f'<UnitTest id="{index}"><TestMethod className="{fqn.rsplit(".", 1)[0]}" name="{fqn.rsplit(".", 1)[1]}"/></UnitTest>'
        for index, (fqn, _) in enumerate(results))
    rows = "".join(f'<UnitTestResult testId="{index}" testName="{fqn}" outcome="{outcome}"/>'
                   for index, (fqn, outcome) in enumerate(results))
    failed = sum(1 for _, outcome in results if outcome == "Failed")
    executed = sum(1 for _, outcome in results if outcome != "NotExecuted")
    return (f'<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">'
            f"<TestDefinitions>{definitions}</TestDefinitions><Results>{rows}</Results>"
            f'<ResultSummary outcome="{"Aborted" if aborted else "Completed"}"><Counters total="{len(results)}" '
            f'executed="{executed}" failed="{failed}" error="0" timeout="0" aborted="{int(aborted)}"/>'
            f"</ResultSummary></TestRun>")


class EvidenceTests(unittest.TestCase):
    def setUp(self):
        self.artifacts = Path(tempfile.mkdtemp(prefix="litedb-evidence-"))

    def tearDown(self):
        shutil.rmtree(self.artifacts, ignore_errors=True)

    def leg(self, runtime, results=None, sha=SHA, discovered=None, job="test-linux", aborted=False):
        directory = self.artifacts / f"test-results-{job}-{runtime}"
        directory.mkdir()
        results = results if results is not None else [(GUARD, "Passed"), (RUNS, "Passed"), (SKIPPED, "NotExecuted")]
        (directory / "TestResults-all.trx").write_text(trx(results, aborted), encoding="utf-8")
        listed = discovered if discovered is not None else [fqn for fqn, _ in results]
        (directory / "discovered-tests.txt").write_text("\n".join(listed), encoding="utf-8")
        (directory / "evidence-leg.json").write_text(json.dumps({
            "job": job, "matrix": {"item": {"runtimeMajor": runtime}}, "sha": sha, "buildSha": sha,
            "format": "trx", "discovery": "discovered-tests.txt", "partitions": {"all": "TestResults-all.trx"}}))

    def run_check(self, needs=NEEDS, quarantine=QUARANTINE, tier="pr"):
        files = {**SOURCE, ".github/safety/ci-evidence.json": json.dumps(CONFIG),
                 ".github/safety/coverage-ledger.json": json.dumps({"quarantine": quarantine, "dispositions": []})}
        with GitRepo() as repo, patch.dict(os.environ, {"NEEDS_JSON": json.dumps(needs)}):
            repo.commit(files)
            return run_quietly(check.main, ["--artifacts", str(self.artifacts), "--tier", tier, "--sha", SHA])

    def test_complete_current_evidence_passes(self):
        self.leg(8)
        self.leg(10)
        code, output = self.run_check()
        self.assertEqual(code, 0, output)
        self.assertIn(f"Quarantined, never executed (visible coverage gaps): 1\n- `{SKIPPED}`", output)

    def test_a_required_job_that_did_not_succeed_fails(self):
        self.leg(8)
        self.leg(10)
        for result in ("skipped", "cancelled", "failure"):
            with self.subTest(result):
                code, output = self.run_check(needs={**NEEDS, "build-linux": {"result": result}})
                self.assertEqual(code, 1)
                self.assertIn(f"Job build-linux finished as '{result}'", output)

    def test_undeclared_jobs_fail(self):
        self.leg(8)
        self.leg(10)
        code, output = self.run_check(needs={**NEEDS, "test-new": {"result": "success"}})
        self.assertEqual(code, 1)
        self.assertIn("Job test-new is not declared", output)

    def test_missing_and_unexpected_legs_fail(self):
        self.leg(8)
        self.leg(9)
        code, output = self.run_check()
        self.assertEqual(code, 1)
        self.assertIn("no evidence for the expected leg item.runtimeMajor=10", output)
        self.assertIn("unexpected leg item.runtimeMajor=9", output)

    def test_no_uploaded_evidence_at_all_fails(self):
        code, output = self.run_check()
        self.assertEqual(code, 1)
        self.assertIn("no evidence for the expected leg item.runtimeMajor=8", output)
        self.assertIn("no evidence for the expected leg item.runtimeMajor=10", output)

    def test_evidence_from_another_revision_fails(self):
        self.leg(8)
        self.leg(10, sha="b" * 40)
        code, output = self.run_check()
        self.assertEqual(code, 1)
        self.assertIn(f"tested {'b' * 40}, not the validated revision", output)

    def test_truncated_run_fails_even_when_every_reported_test_passed(self):
        self.leg(8)
        self.leg(10, results=[(GUARD, "Passed"), (RUNS, "Passed")], discovered=[GUARD, RUNS, SKIPPED])
        code, output = self.run_check()
        self.assertEqual(code, 1)
        self.assertIn(f"1 discovered tests produced no result (first: {SKIPPED})", output)

    def test_empty_aborted_or_guardless_partitions_fail(self):
        self.leg(8, results=[])
        self.leg(10, results=[(RUNS, "Passed")], aborted=True)
        code, output = self.run_check()
        self.assertEqual(code, 1)
        self.assertIn("partition all reported no tests", output)
        self.assertIn("did not pass cleanly (Aborted", output)
        self.assertIn("runtime/architecture guard", output)

    def test_a_test_no_leg_executed_needs_a_quarantine_entry(self):
        self.leg(8)
        self.leg(10)
        code, output = self.run_check(quarantine=[])
        self.assertEqual(code, 1)
        self.assertIn(f"{SKIPPED} was skipped on every leg", output)

    def test_a_source_test_no_leg_discovered_needs_a_quarantine_entry(self):
        results = [(GUARD, "Passed"), (RUNS, "Passed")]
        self.leg(8, results=results)
        self.leg(10, results=results)
        code, output = self.run_check(quarantine=[])
        self.assertEqual(code, 1)
        self.assertIn(f"{SKIPPED} was not discovered on any leg", output)

    def test_a_quarantined_test_that_ran_is_reported_as_stale(self):
        self.leg(8, results=[(GUARD, "Passed"), (RUNS, "Passed"), (SKIPPED, "Passed")])
        self.leg(10)
        code, output = self.run_check()
        self.assertEqual(code, 0, output)
        self.assertIn(f"Quarantined test {SKIPPED} executed on a leg", output)


if __name__ == "__main__":
    unittest.main()
