"""Check the hosted bootstrap before granting its immutable controller a token."""

import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import Mock, patch

import yaml


WORKFLOW = Path(__file__).resolve().parents[1] / "workflows/bugfix-sweep.yml"


class HostedSweepWorkflowTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.workflow = yaml.load(WORKFLOW.read_text(encoding="utf-8"), Loader=yaml.BaseLoader)
        cls.job = cls.workflow["jobs"]["tick"]
        cls.steps = {step["name"]: step for step in cls.job["steps"]}

    def env(self, **changes):
        values = {"SCHEDULER_SHA": "a" * 40, "ACTIVE_SWEEP": "canary", "REQUESTED_SWEEP": "",
                  "OPERATION": "tick", "WORKFLOW_SHA": "b" * 40,
                  "WORKFLOW_REF": "automation/bugfix-runtime-v9", "CAMPAIGN_PREFIX": "hosted-v9",
                  "ISSUES": "2802 2770", "REASON": "Operator pause", "SWEEP": "canary",
                  "GITHUB_REPOSITORY": "litedb-org/LiteDB", "GITHUB_RUN_ID": "123",
                  "GITHUB_RUN_ATTEMPT": "2", "GITHUB_WORKSPACE": "/trusted"}
        return {**values, **changes}

    def validate(self, **changes):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "output"
            env = self.env(GITHUB_OUTPUT=str(output), **changes)
            with patch.dict(os.environ, env, clear=True):
                exec(compile(self.steps["Validate scheduler configuration"]["run"], "configuration", "exec"), {})
            return output.read_text(encoding="utf-8")

    def test_default_branch_only_and_same_repository_wakeups(self):
        condition = self.job["if"]
        self.assertIn("github.event.repository.default_branch", condition)
        self.assertIn("github.repository == 'litedb-org/LiteDB'", condition)
        self.assertIn("head_repository.full_name == github.repository", condition)
        self.assertIn("workflow_run.event == 'workflow_dispatch'", condition)
        self.assertIn("vars.BUGFIX_SWEEP_ENABLED == 'true'", condition)
        self.assertNotIn("push", self.workflow["on"])
        self.assertEqual([{"cron": "2-59/5 * * * *"}], self.workflow["on"]["schedule"])
        wakeups = self.workflow["on"]["workflow_run"]
        self.assertEqual(["completed"], wakeups["types"])
        self.assertEqual(["automation/bugfix-runtime-*"], wakeups["branches"])
        self.assertEqual({"Wholesale bugfix checks", "Wholesale bugfix fix worker",
                          "Wholesale bugfix validation worker"}, set(wakeups["workflows"]))

    def test_permissions_concurrency_and_execution_bounds(self):
        self.assertEqual({"contents": "write", "actions": "write", "issues": "write"}, self.workflow["permissions"])
        self.assertEqual({"group": "wholesale-bugfix-global-writer", "cancel-in-progress": "false"},
                         self.workflow["concurrency"])
        self.assertEqual("10", self.job["timeout-minutes"])
        raw = WORKFLOW.read_text(encoding="utf-8")
        self.assertNotIn("secrets.", raw)
        self.assertNotIn("serial_queue.py", raw)
        self.assertNotIn("bugfix-full-ci", raw)

    def test_validates_immutable_sha_before_checkout_and_controller_execution(self):
        names = list(self.steps)
        self.assertLess(names.index("Validate scheduler configuration"), names.index("Checkout immutable scheduler"))
        self.assertLess(names.index("Checkout immutable scheduler"), names.index("Advance one bounded scheduler operation"))
        checkout = self.steps["Checkout immutable scheduler"]
        self.assertEqual("${{ steps.configuration.outputs.sha }}", checkout["with"]["ref"])
        self.assertEqual("false", checkout["with"]["persist-credentials"])
        self.assertRegex(checkout["uses"], r"^actions/checkout@[0-9a-f]{40}$")
        self.assertEqual("sha=" + "a" * 40 + "\nsweep=canary\n", self.validate())
        for invalid in ("dev", "a" * 39, "a" * 40 + "\nsweep=another"):
            with self.subTest(invalid=invalid), self.assertRaises(SystemExit):
                self.validate(SCHEDULER_SHA=invalid)

    def test_rejects_unsafe_or_incomplete_controls(self):
        for changes in ({"REQUESTED_SWEEP": "../other"}, {"OPERATION": "shell"},
                        {"OPERATION": "init", "WORKFLOW_REF": "dev"},
                        {"OPERATION": "init", "ISSUES": "1; echo bad"},
                        {"OPERATION": "init", "ISSUES": "1 1"},
                        {"OPERATION": "init", "ISSUES": ""},
                        {"OPERATION": "pause", "REASON": ""}):
            with self.subTest(changes=changes), self.assertRaises(SystemExit):
                self.validate(**changes)
        self.validate(OPERATION="init", ISSUES=" ".join(str(value) for value in range(1, 41)))

    def test_arguments_are_literal_and_owner_attempt_is_preserved(self):
        script = self.steps["Advance one bounded scheduler operation"]["run"]
        self.assertNotIn("${{", script)
        for operation in ("init", "tick", "pause", "resume", "status"):
            with self.subTest(operation=operation), tempfile.TemporaryDirectory() as directory:
                env = self.env(OPERATION=operation, REASON="literal $(echo unsafe); reason\nnext line",
                               GITHUB_STEP_SUMMARY=str(Path(directory) / "summary"))
                with patch.dict(os.environ, env, clear=True), \
                        patch("subprocess.check_output", return_value="a" * 40 + "\n"), \
                        patch("subprocess.run", return_value=Mock(returncode=0)) as run, \
                        self.assertRaises(SystemExit) as result:
                    exec(compile(script, "operation", "exec"), {})
                self.assertEqual(0, result.exception.code)
                args = run.call_args.args[0]
                self.assertEqual([".github/bugfix/sweep.py", operation], args[1:3])
                self.assertEqual({"check": False, "timeout": 480}, run.call_args.kwargs)
                if operation != "status":
                    self.assertEqual("2", args[args.index("--owner-run-attempt") + 1])
                    self.assertEqual("a" * 40, args[args.index("--scheduler-sha") + 1])
                if operation in ("pause", "resume"):
                    self.assertEqual(env["REASON"], args[args.index("--reason") + 1])

    def test_wrong_checkout_never_executes_controller(self):
        script = self.steps["Advance one bounded scheduler operation"]["run"]
        with patch.dict(os.environ, self.env(), clear=True), \
                patch("subprocess.check_output", return_value="b" * 40 + "\n"), \
                patch("subprocess.run") as run, self.assertRaises(SystemExit):
            exec(compile(script, "operation", "exec"), {})
        run.assert_not_called()

    def test_status_publisher_requires_successful_trusted_checkout(self):
        report = self.steps["Publish durable sweep status"]
        self.assertEqual("always() && steps.checkout.outcome == 'success'", report["if"])
        self.assertIn("--issue 2890", report["run"])
        self.assertEqual("${{ github.token }}", report["env"]["GH_TOKEN"])


if __name__ == "__main__":
    unittest.main()
