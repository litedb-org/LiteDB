#!/usr/bin/env python3
"""Protect generated workers against gh-aw's default issue-publishing behavior."""

import unittest
from pathlib import Path

from check_gh_aw_readonly import WORKERS, validate_readonly
from patch_gh_aw_codex_endpoint import CODEX_EXEC_COMMAND, HIGH_REASONING_OVERRIDE, RUNTIME_PROBE_STEP, SINGLE_AGENT_OVERRIDES


class ReadonlyWorkerTests(unittest.TestCase):
    def setUp(self):
        self.fixture = """permissions: {}
jobs:
  agent:
    permissions:
      contents: read
    steps:
      - run: |
          Tools: noop, record_completion
"""

    def test_actual_compiled_workers_have_no_write_permissions_or_tools(self):
        workflows = Path(__file__).resolve().parents[1] / "workflows"
        for name in sorted(WORKERS):
            with self.subTest(worker=name):
                validate_readonly((workflows / name).read_text(encoding="utf-8"))

    def test_workers_allow_only_controller_bot_and_keep_human_role_gate(self):
        workflows = Path(__file__).resolve().parents[1] / "workflows"
        for name in sorted(WORKERS):
            with self.subTest(worker=name):
                text = (workflows / name).read_text(encoding="utf-8")
                bots = [line.strip() for line in text.splitlines() if "GH_AW_ALLOWED_BOTS:" in line]
                roles = [line.strip() for line in text.splitlines() if "GH_AW_REQUIRED_ROLES:" in line]
                if name == 'bugfix-agent-canary.lock.yml':
                    # Dispatch authorization is enforced by GitHub itself. The
                    # compiler omits pre-activation for dispatch-only workflows.
                    self.assertIn('\n  workflow_dispatch:', text)
                    self.assertNotIn('\n  push:', text)
                    self.assertNotIn('\n  pre_activation:', text)
                    continue
                self.assertTrue(bots, "Controller bot must be allowed through activation")
                self.assertEqual({'GH_AW_ALLOWED_BOTS: "github-actions[bot]"'}, set(bots))
                self.assertTrue(roles, "Human role checks must remain enabled")
                self.assertEqual({'GH_AW_REQUIRED_ROLES: "admin,maintainer,write"'}, set(roles))

    def test_workers_pin_models_and_disable_internal_delegation(self):
        workflows = Path(__file__).resolve().parents[1] / "workflows"
        for name in sorted(WORKERS):
            with self.subTest(worker=name):
                text = (workflows / name).read_text(encoding="utf-8")
                model = "gpt-5.6-sol" if name == "bugfix-validate.lock.yml" else "gpt-6-astra"
                self.assertIn(f"GH_AW_MODEL_AGENT_CODEX: {model}\n", text)
                self.assertIn('model_reasoning_effort = "high"', text)
                commands = [line for line in text.splitlines() if CODEX_EXEC_COMMAND in line]
                self.assertEqual(1, len(commands), "Each worker must have exactly one model process")
                self.assertNotIn("agents.enabled", commands[0])
                self.assertIn(HIGH_REASONING_OVERRIDE, commands[0])
                install = text.index("npm install --ignore-scripts -g @openai/codex@0.154.0")
                probe = text.index(f"name: {RUNTIME_PROBE_STEP}")
                execute = text.index("name: Execute Codex CLI")
                self.assertLess(install, probe)
                self.assertLess(probe, execute)
                for override in SINGLE_AGENT_OVERRIDES:
                    self.assertEqual(1, commands[0].count(override))

    def test_accepts_readonly_generated_structure(self):
        validate_readonly(self.fixture)

    def test_rejects_writable_permissions_in_any_job(self):
        for scope in ("contents", "issues", "pull-requests", "actions", "discussions", "id-token"):
            with self.subTest(scope=scope):
                malicious = self.fixture + f"  outputs:\n    permissions:\n      {scope}: write\n"
                with self.assertRaisesRegex(ValueError, "permissions"):
                    validate_readonly(malicious)

    def test_rejects_inline_and_dynamic_permissions(self):
        for value in ("write-all", "{issues: write}", "${{ inputs.permissions }}"):
            with self.subTest(value=value):
                with self.assertRaisesRegex(ValueError, "permissions"):
                    validate_readonly(self.fixture.replace("permissions: {}", f"permissions: {value}"))

    def test_rejects_default_issue_tool_even_without_write_permissions(self):
        with self.assertRaisesRegex(ValueError, "publishing"):
            validate_readonly(self.fixture.replace("Tools: noop, record_completion", "Tools: noop, create_issue"))

    def test_rejects_incomplete_failure_issue_handler(self):
        with self.assertRaisesRegex(ValueError, "publication"):
            validate_readonly(self.fixture + '\n# create_report_incomplete_issue handler\n')


if __name__ == "__main__":
    unittest.main()
