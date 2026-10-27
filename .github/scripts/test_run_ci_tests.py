"""Exercise the real PowerShell runner with deterministic VSTest process results.

Run with: python -m unittest discover -s .github/scripts -p test_run_ci_tests.py
Requires pwsh and dotnet (also installed in the CI policy-test job). No database
tests or runtime downloads run; each case copies the runner into a temp tree.
"""
import json
import os
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path


SCRIPT = Path(__file__).resolve().parents[2] / "scripts/run-ci-tests.ps1"
GUARDS = ("RequestedRuntimeAndArchitecture_AreActuallyRunning",
          "LoadedLibrary_ContainsTheRequiredEngineTestHooks")

MOCK = r'''
import json, os, sys
from pathlib import Path
root = Path(os.environ['CI_RUNNER_PROBE'])
args = sys.argv[1:]
if '/ListFullyQualifiedTests' in args:
    target = next(a.split(':', 1)[1] for a in args if a.startswith('/ListTestsTargetPath:'))
    Path(target).write_text('LiteDB.Tests.Issues.Probe.Test\n')
    sys.exit(0)
calls = root / 'calls.json'
history = json.loads(calls.read_text()) if calls.exists() else []
history.append(args)
calls.write_text(json.dumps(history))
attempts = json.loads((root / 'attempts.json').read_text())
attempt = attempts[min(len(history) - 1, len(attempts) - 1)]
if attempt['guards'] is not None:
    directory = next(a.split(':', 1)[1] for a in args if a.startswith('/ResultsDirectory:'))
    name = next(a.split('LogFileName=', 1)[1] for a in args if a.startswith('/Logger:trx;'))
    result = Path(directory) / name
    result.parent.mkdir(parents=True, exist_ok=True)
    result.write_text('<TestRun><Results>' + ''.join(
        '<UnitTestResult testName="' + name + '" outcome="Passed"/>'
        for name in attempt['guards']) + '</Results></TestRun>')
for arg in args:
    if arg.startswith('/Diag:'):
        log = Path(arg.split(':', 1)[1])
        log.parent.mkdir(parents=True, exist_ok=True)
        log.write_text('probe diagnostic log')
print('Simulated VSTest after test entry: exit', attempt['exit'])
sys.exit(attempt['exit'])
'''


def ps_quote(value):
    return "'" + str(value).replace("'", "''") + "'"


def attempt(code=0, guards=GUARDS):
    return {"exit": code, "guards": guards}


class RunCiTestsTests(unittest.TestCase):
    def run_runner(self, attempts, partition=False, stale=False):
        with tempfile.TemporaryDirectory(prefix="litedb-ci-runner-") as temporary:
            root = Path(temporary)
            scripts = root / "scripts"
            scripts.mkdir()
            shutil.copyfile(SCRIPT, scripts / SCRIPT.name)
            (root / "attempts.json").write_text(json.dumps(attempts))
            (root / "mock.py").write_text(MOCK)
            if stale:
                results = root / "LiteDB.Tests/TestResults"
                results.mkdir(parents=True)
                (results / "probe.trx").write_text("stale results")
            dotnet = shutil.which("dotnet")
            self.assertIsNotNone(dotnet, "dotnet is required for the runner's --info check")
            harness = root / "probe.ps1"
            harness.write_text(
                "function global:dotnet {\n"
                f"    & {ps_quote(sys.executable)} {ps_quote(root / 'mock.py')} @args\n"
                "    $global:LASTEXITCODE = $LASTEXITCODE\n}\n"
                f"& {ps_quote(scripts / SCRIPT.name)} -RuntimeMajor 10 -Framework net10.0 "
                f"-RuntimeDirectory {ps_quote(Path(dotnet).parent)} "
                + ("-PartitionSuite\n" if partition else
                   "-Filter 'FullyQualifiedName~Probe' -ResultFile probe.trx\n")
                + "exit $LASTEXITCODE\n")
            env = dict(os.environ, CI_RUNNER_PROBE=str(root), LITEDB_CI_TMPDIR=str(root))
            run = subprocess.run(["pwsh", "-NoProfile", "-File", str(harness)],
                                 env=env, capture_output=True, text=True, timeout=45)
            output = run.stdout + run.stderr
            self.assertNotIn("Traceback (most recent call last)", output)
            calls = json.loads((root / "calls.json").read_text())
            return run.returncode, output, calls

    def test_missing_trx_crash_stays_failed_after_green_retry(self):
        code, output, calls = self.run_runner([attempt(139, None), attempt()])
        self.assertEqual(139, code, output)
        self.assertEqual(2, len(calls))
        self.assertIn("::error::VSTest originally exited with 139", output)
        self.assertIn("Diagnostic retry exited with 0; this partition remains failed", output)
        self.assertIn("probe diagnostic log", output)
        self.assertTrue(any(arg.startswith("/Diag:") for arg in calls[1]))
        self.assertEqual(calls[0], [arg for arg in calls[1] if not arg.startswith("/Diag:")])

    def test_retry_failure_does_not_replace_original_exit(self):
        for retry in (attempt(7, None), attempt(1)):
            with self.subTest(retry=retry):
                code, output, calls = self.run_runner([attempt(139, None), retry])
                self.assertEqual(139, code, output)
                self.assertEqual(2, len(calls))
                self.assertIn(
                    f"Diagnostic retry exited with {retry['exit']}; this partition remains failed",
                    output)
                self.assertIn("probe diagnostic log", output)

    def test_failure_with_trx_does_not_retry(self):
        code, output, calls = self.run_runner([attempt(1)])
        self.assertEqual(1, code, output)
        self.assertEqual(1, len(calls))

    def test_clean_run_with_guards_passes_without_retry(self):
        code, output, calls = self.run_runner([attempt()])
        self.assertEqual(0, code, output)
        self.assertEqual(1, len(calls))

    def test_missing_guard_still_fails(self):
        code, output, calls = self.run_runner([attempt(0, GUARDS[:1])])
        self.assertNotEqual(0, code, output)
        self.assertIn("Required CI guard did not pass", output)
        self.assertEqual(1, len(calls))

    def test_stale_trx_cannot_hide_missing_results(self):
        code, output, calls = self.run_runner([attempt(139, None), attempt()], stale=True)
        self.assertEqual(139, code, output)
        self.assertEqual(2, len(calls))

    def test_partition_suite_propagates_failure_and_runs_remaining_partitions(self):
        code, output, calls = self.run_runner([attempt(139, None), attempt()], partition=True)
        self.assertNotEqual(0, code, output)
        self.assertIn("Test partitions failed: issues", output)
        # One diagnostic retry, plus one execution per declared partition.
        results = [next(arg for arg in call if arg.startswith('/Logger:trx;')) for call in calls]
        self.assertEqual(2, results.count('/Logger:trx;LogFileName=TestResults-issues.trx'))
        self.assertIn('/Logger:trx;LogFileName=TestResults-remaining.trx', results)
        self.assertEqual(len(set(results)) + 1, len(results))


if __name__ == "__main__":
    unittest.main()
