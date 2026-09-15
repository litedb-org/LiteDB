"""The CI entry point enforces accepted cases before constructing a failure ledger."""

import contextlib
import importlib.util
import io
import json
import os
from pathlib import Path
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

from state import Rejected
from test_profiles import profile_fixture


class PassingGraderTests(unittest.TestCase):
    def run_grader(self, level, failing_variant=None):
        source = Path(__file__).resolve().parents[1] / "scripts/grade_bugfix_checks.py"
        spec = importlib.util.spec_from_file_location("_passing_grader_test", source)
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            control = root / "control"
            manifest = control / "scripts/bugfix/issues.json"
            manifest.parent.mkdir(parents=True)
            manifest.write_text(json.dumps({"issues": {"2000": {"frozen_test_revision": "b" * 40}}}))
            snapshot = {"schema_version": 1, "state_commit": "d" * 40, "ledger_sha256": "e" * 64,
                        "cases_sha256": "f" * 64, "case_count": 1}
            name = "LiteDB.Tests.Issue1000_Tests.Previously_fixed"
            calls, reads = [], []

            def call(arguments):
                calls.append(list(map(str, arguments)))
                if "run_bugfix_tests.py" in str(arguments[0]):
                    output = Path(arguments[arguments.index("--output") + 1])
                    output.mkdir(parents=True)
                    (output / "execution.json").write_text(json.dumps({"runs": {"focused": 1, "broad": 0, "required-pass": 0}}))
                if "compare" in arguments:
                    Path(arguments[arguments.index("--output") + 1]).write_text('{"source_context_changes": []}')

            def read(path, exit_code):
                reads.append(path)
                outcome = "Failed" if path.parent.name == failing_variant else "Passed"
                return SimpleNamespace(tests={"opaque": SimpleNamespace(name=name, outcome=outcome)})

            environment = {"ISSUE": "2000", "BASE_SHA": "a" * 40, "CANDIDATE_SHA": "c" * 40 if level != "baseline" else "",
                           "LEVEL": level, "FRAMEWORK": "net8.0", "GITHUB_SHA": "9" * 40, "GITHUB_REPOSITORY": "owner/repo",
                           "ACCEPTED_STATE_SHA": "d" * 40, "ACCEPTED_LEDGER_SHA256": "e" * 64}
            profile = profile_fixture("c" * 40, issue=2000)
            environment["ACCEPTANCE_PROFILE_SHA256"] = profile["profile_sha256"]
            with patch.multiple(module, ROOT=root, CONTROL=control, ARTIFACTS=root / "artifacts", MANIFEST=manifest), \
                    patch.object(module, "call", side_effect=call), patch("passing.load_snapshot", return_value=(snapshot, [name])), \
                    patch("profiles.build_profile", return_value=profile), \
                    patch.dict(sys.modules, {"trx": SimpleNamespace(read_trx=read)}), patch.object(sys, "path", sys.path.copy()), \
                    patch.dict(os.environ, environment), contextlib.redirect_stdout(io.StringIO()):
                if failing_variant:
                    with self.assertRaisesRegex(Rejected, "Previously accepted tests regressed"):
                        module.main()
                    self.assertFalse((root / "artifacts/verdict.json").exists())
                    self.assertFalse(any("snapshot" in command for command in calls))
                    return
                module.main()
            report = json.loads((root / "artifacts/verdict.json").read_text())
            self.assertTrue(report["previously_accepted_tests_passed"])
            self.assertEqual(snapshot, report["passing_contract"])
            runners = [command for command in calls if "run_bugfix_tests.py" in command[0]]
            if level == "baseline":
                self.assertIn("--required-pass-filter", runners[0])
                self.assertEqual("required-pass.trx", reads[0].name)
            else:
                self.assertTrue(all("--required-pass-filter" not in command for command in runners))
                self.assertEqual(["broad.trx", "broad.trx"], [path.name for path in reads])

    def test_baseline_checks_prior_contract_in_separate_selected_run(self):
        self.run_grader("baseline")

    def test_broad_checks_prior_contract_in_full_baseline_and_candidate_runs(self):
        self.run_grader("broad")

    def test_prior_failure_cannot_be_grandfathered_into_baseline_ledger(self):
        for variant in ("baseline", "candidate"):
            with self.subTest(variant=variant):
                self.run_grader("broad", failing_variant=variant)


if __name__ == "__main__":
    unittest.main()
