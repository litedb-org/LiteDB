"""Production reports exist only after every selected command succeeds."""

import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

import production
from test_profiles import profile_fixture


class ProductionCommandsTests(unittest.TestCase):
    def run_build(self, root, compatibility=False, failure=False):
        profile = profile_fixture()
        profile["compatibility"] = compatibility
        manifest = root / "control/scripts/bugfix/issues.json"
        manifest.parent.mkdir(parents=True, exist_ok=True)
        manifest.write_text(json.dumps({"issues": {"2874": {"frozen_test_revision": "b" * 40}}}))
        output = root / "artifacts/production.json"
        args = ["production.py", "--repository", str(root / "candidate"), "--control", str(root / "control"),
                "--issue", "2874", "--base-sha", "a" * 40, "--candidate-sha", "d" * 40,
                "--profile-sha256", profile["profile_sha256"], "--output", str(output)]
        commands = []
        def execute(command, **kwargs):
            commands.append(command)
            self.assertEqual(root / "candidate", kwargs["cwd"])
            self.assertTrue(kwargs["check"])
            if failure and command[0] == "dotnet":
                raise subprocess.CalledProcessError(1, command)
        with patch("sys.argv", args), patch.dict("os.environ", {"GITHUB_SHA": "c" * 40}), \
                patch("production.git", side_effect=["c" * 40, "d" * 40]), \
                patch("production.build_profile", return_value=profile), \
                patch("production.subprocess.run", side_effect=execute):
            production.main()
        return commands, json.loads(output.read_bytes())

    def test_build_targets_and_optional_compatibility_are_separate_from_test_hooks(self):
        for compatibility in (False, True):
            with self.subTest(compatibility=compatibility), tempfile.TemporaryDirectory() as directory:
                commands, report = self.run_build(Path(directory), compatibility)
                self.assertEqual(3 if compatibility else 2, len(commands))
                self.assertIn("-p:TestingEnabled=false", commands[1])
                self.assertNotIn("--framework", commands[1])
                self.assertEqual(compatibility, report["compatibility"])
                self.assertTrue(report["accepted"])

    def test_failed_build_never_writes_positive_report(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            with self.assertRaises(subprocess.CalledProcessError):
                self.run_build(root, failure=True)
            self.assertFalse((root / "artifacts/production.json").exists())
            report = json.loads((root / "artifacts/production-build-report.json").read_bytes())
            self.assertEqual("harness_error", report["outcome"])


if __name__ == "__main__":
    unittest.main()
