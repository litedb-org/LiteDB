"""The test build runner retains failure evidence before it exits without TRX."""

import importlib.util
import hashlib
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch


class RunnerBuildEvidenceTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        source = Path(__file__).resolve().parents[1] / "scripts/run_bugfix_tests.py"
        spec = importlib.util.spec_from_file_location("compiler_test_runner", source)
        cls.runner = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(cls.runner)

    def test_vstest_diagnostic_binds_the_actual_testhost_architecture(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "vstest-diag.txt"
            line = (b"TpTrace: DotnetTestHostmanager.GetTestHostProcessStartInfo: "
                    b"Platform environment 'X86' target architecture 'X86' "
                    b"framework '.NETCoreApp,Version=v8.0' OS 'Windows'\n")
            path.write_bytes(line)
            self.assertEqual({
                "testhost_architecture": "x86",
                "sha256": hashlib.sha256(line).hexdigest(),
            }, self.runner.read_diagnostic(path))
            path.write_bytes(line + line.replace(b"X86", b"ARM64"))
            with self.assertRaisesRegex(ValueError, "one exact testhost"):
                self.runner.read_diagnostic(path)

    def test_compiler_failure_and_timeout_are_retained_before_test_execution(self):
        runner = self.runner
        for timeout in (False, True):
            with self.subTest(timeout=timeout), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                manifest = root / "issues.json"
                manifest.write_text(json.dumps({"issues": {"2874": {"frozen_test_revision": "b" * 40}}}))
                output = root / "output"
                args = ["runner", "--repository", str(root), "--output", str(output),
                        "--manifest", str(manifest), "--issue", "2874"]
                def execute(command, repository, log, limit):
                    log.write_text(f"{root}/LiteDB/Document/ObjectId.cs(12,4): error CS1002: ; expected\n", encoding="utf-8")
                    if timeout:
                        raise subprocess.TimeoutExpired(command, limit)
                    return 1
                with patch("sys.argv", args), patch.dict("os.environ", {"GITHUB_SHA": "c" * 40}), \
                        patch.object(runner.subprocess, "check_output", return_value="d" * 40), \
                        patch.object(runner.subprocess, "run"), patch.object(runner, "execute", side_effect=execute):
                    with self.assertRaises(subprocess.TimeoutExpired if timeout else RuntimeError):
                        runner.main()
                report = json.loads((output / "build-report.json").read_bytes())
                self.assertEqual("harness_error" if timeout else "compiler_error", report["outcome"])
                self.assertEqual("d" * 40, report["source_sha"])
                self.assertEqual("b" * 40, report["test_source_sha"])
                self.assertEqual(timeout, report["timed_out"])
                self.assertFalse((output / "focused.trx").exists())


if __name__ == "__main__":
    unittest.main()
