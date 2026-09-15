"""Only a trusted, explicit daily-limit decision may defer the hosted sweep."""

import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

from patch_gh_aw_budget_evidence import MARKER, RUNTIME_SCRIPT, evidence_steps, insert_budget_evidence


class BudgetEvidenceTests(unittest.TestCase):
    def report(self, **changes):
        with tempfile.TemporaryDirectory() as directory:
            env = {'BUDGET_EXCEEDED': 'true', 'BUDGET_THRESHOLD': '5000', 'BUDGET_TOTAL': '5200.5',
                   'BUDGET_WORKFLOW_SHA': 'a' * 40, 'GITHUB_RUN_ID': '123', 'GITHUB_RUN_ATTEMPT': '2',
                   'RUNNER_TEMP': directory, **changes}
            with patch.dict(os.environ, env, clear=True):
                exec(compile(RUNTIME_SCRIPT, 'budget-evidence', 'exec'), {})
            return json.loads((Path(directory) / 'bugfix-budget/budget-status.json').read_bytes())

    def test_report_binds_exact_run_attempt_and_workflow(self):
        report = self.report()
        self.assertEqual({'schema_version', 'run_id', 'run_attempt', 'workflow_sha', 'exceeded',
                          'threshold', 'total', 'observed_at'}, set(report))
        self.assertEqual(123, report['run_id'])
        self.assertEqual(2, report['run_attempt'])
        self.assertEqual('a' * 40, report['workflow_sha'])
        self.assertIs(True, report['exceeded'])
        self.assertEqual(5000, report['threshold'])
        self.assertEqual(5200.5, report['total'])
        self.assertRegex(report['observed_at'], r'^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$')

    def test_missing_or_non_exceeded_or_invalid_accounting_fails_closed(self):
        cases = ({'BUDGET_EXCEEDED': ''}, {'BUDGET_EXCEEDED': 'false'}, {'BUDGET_THRESHOLD': '0'},
                 {'BUDGET_THRESHOLD': '-1'}, {'BUDGET_THRESHOLD': 'NaN'}, {'BUDGET_TOTAL': 'Infinity'},
                 {'BUDGET_TOTAL': '4999'}, {'BUDGET_WORKFLOW_SHA': 'main'}, {'GITHUB_RUN_ID': '0'},
                 {'GITHUB_RUN_ATTEMPT': '-2'})
        for changes in cases:
            with self.subTest(changes=changes), self.assertRaises(SystemExit):
                self.report(**changes)

    def test_patch_is_idempotent_and_only_uses_trusted_job_outputs(self):
        fixture = ['  activation:', '    outputs:', '      daily_ai_credits_exceeded: value',
                   '      daily_ai_credits_threshold: value', '      daily_ai_credits_total_effective_tokens: value',
                   '  agent:', '    steps: []', '  conclusion:', '    steps:', '      - name: Handle agent failure']
        path = Path('bugfix-fix.lock.yml')
        result = insert_budget_evidence(path, fixture)
        self.assertEqual(result, insert_budget_evidence(path, result))
        self.assertGreater(result.index(MARKER), result.index('  conclusion:'))
        block = '\n'.join(evidence_steps())
        self.assertIn("needs.activation.outputs.daily_ai_credits_exceeded == 'true'", block)
        self.assertNotIn('secrets.', block)
        self.assertNotIn('needs.agent.', block)
        self.assertNotIn('/tmp/gh-aw/bugfix/', block)
        for broken in ([], fixture[:-1], fixture + ['      - name: Handle agent failure']):
            with self.assertRaises(RuntimeError):
                insert_budget_evidence(path, broken)

    def test_actual_worker_locks_emit_evidence_from_conclusion(self):
        workflows = Path(__file__).resolve().parents[1] / 'workflows'
        for name in ('bugfix-fix.lock.yml', 'bugfix-validate.lock.yml'):
            lines = (workflows / name).read_text(encoding='utf-8').splitlines()
            self.assertEqual(lines, insert_budget_evidence(Path(name), lines))
            self.assertEqual(1, lines.count(MARKER))


if __name__ == '__main__':
    unittest.main()
