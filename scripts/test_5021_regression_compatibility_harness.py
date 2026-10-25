"""Failure-path checks for the bounded released-fixture production probe."""
import importlib.util
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch
import zipfile


SCRIPT = Path(__file__).with_name("test-5021-regression-compatibility.py")
spec = importlib.util.spec_from_file_location("regression_compatibility", SCRIPT)
compatibility = importlib.util.module_from_spec(spec)
spec.loader.exec_module(compatibility)


class RegressionCompatibilityHarnessTests(unittest.TestCase):
    def test_hung_probe_reports_failure_and_removes_its_worktree(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            archive = root / "fixture.zip"
            with zipfile.ZipFile(archive, "w"):
                pass
            runs = root / "runs"
            runs.mkdir()
            commands = []

            def run(command, **kwargs):
                commands.append((command, kwargs))
                if command[:3] == ["git", "worktree", "add"]:
                    assembly = Path(command[4]) / "LiteDB/bin/Release/net8.0/LiteDB.dll"
                    assembly.parent.mkdir(parents=True)
                    assembly.write_bytes(b"fake production assembly; no engine code executes")
                if command[0] == "dotnet" and command[1].endswith("Probe.dll"):
                    self.assertEqual(600, kwargs.get("timeout"))
                    raise subprocess.TimeoutExpired(command, kwargs["timeout"])
                return subprocess.CompletedProcess(command, 0)

            with patch.object(compatibility.subprocess, "run", side_effect=run), \
                    patch.object(compatibility, "artifact", return_value=archive), \
                    patch("sys.argv", [str(SCRIPT), "--temp-root", str(runs)]):
                with self.assertRaisesRegex(SystemExit, "did not finish within 600 s"):
                    compatibility.main()

            removed = [command for command, _ in commands if command[:3] == ["git", "worktree", "remove"]]
            self.assertEqual(1, len(removed), "the timed-out probe must reach worktree cleanup")
            self.assertEqual([], list(runs.iterdir()), "temporary build and fixture copies must also be removed")


if __name__ == "__main__":
    unittest.main()
