#!/usr/bin/env python3
"""Protect generated workers against gh-aw's default issue-publishing behavior."""

import unittest
from pathlib import Path

from check_gh_aw_readonly import WORKERS, validate_readonly


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
