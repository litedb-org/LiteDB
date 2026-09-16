"""Positive proxy usage survives failure paths and the legacy daily-cache format."""

import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

from patch_gh_aw_accounting import ACCOUNT_SCRIPT, CANONICAL_SCRIPT, insert_accounting


class AccountingTests(unittest.TestCase):
    def account(self, records=None, outcome='success', raw=None):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / 'token-usage.jsonl'
            if records is not None or raw is not None:
                source.write_text(raw if raw is not None else '\n'.join(json.dumps(record) for record in records), encoding='utf-8')
            script = ACCOUNT_SCRIPT.replace("Path('/tmp/gh-aw/sandbox/firewall/logs/api-proxy-logs/token-usage.jsonl')",
                                            f'Path({str(source)!r})')
            env = {'BUGFIX_EXECUTION_OUTCOME': outcome,
                   'GITHUB_OUTPUT': str(root / 'output'), 'GITHUB_STEP_SUMMARY': str(root / 'summary')}
            with patch.dict(os.environ, env, clear=True):
                exec(compile(script, 'accounting', 'exec'), {})
            output = dict(line.split('=', 1) for line in (root / 'output').read_text().splitlines())
            return float(output['aic']), output['source']

    def record(self, value):
        return {'event': 'token_usage', 'model': 'gpt-6-astra', 'ai_credits_total': value}

    def test_exact_positive_cumulative_total_not_sum_survives_failed_execution(self):
        records = [self.record(1), self.record(900), self.record(1209.1325)]
        for outcome in ('success', 'failure', 'cancelled'):
            with self.subTest(outcome=outcome):
                self.assertEqual((1209.1325, 'proxy_total'), self.account(records, outcome))

    def test_never_clamps_observed_overshoot_to_limit(self):
        self.assertEqual((2100, 'proxy_total'), self.account([self.record(2100)]))

    def test_missing_empty_or_invalid_usage_is_explicitly_unavailable(self):
        for records in (None, [], [self.record(-1)], [self.record(float('nan'))],
                        [self.record(float('inf'))], [self.record(True)], [self.record('1200')],
                        [self.record(0)], [{'event': 'token_usage'}]):
            with self.subTest(records=records):
                self.assertEqual((0, 'accounting_unavailable'), self.account(records, 'failure'))

    def test_malformed_record_preserves_larger_valid_observation(self):
        raw = json.dumps(self.record(2400)) + '\nnot json\n'
        self.assertEqual((2400, 'accounting_unavailable'), self.account(raw=raw))

    def test_execution_that_never_started_can_have_zero_cost(self):
        self.assertEqual((0, 'not_started'), self.account(outcome='skipped'))
        self.assertEqual((12, 'proxy_total'), self.account([self.record(12)], outcome='skipped'))

    def canonical(self, amount, source='proxy_total', result='success'):
        with tempfile.TemporaryDirectory() as directory:
            script = CANONICAL_SCRIPT.replace("Path('/tmp/gh-aw')", f'Path({directory!r})')
            env = {'BUGFIX_ACCOUNTED_CREDITS': amount,
                   'BUGFIX_ACCOUNTING_SOURCE': source, 'BUGFIX_AGENT_RESULT': result,
                   'GITHUB_RUN_ID': '123', 'GITHUB_RUN_ATTEMPT': '1'}
            with patch.dict(os.environ, env, clear=True):
                exec(compile(script, 'canonical-accounting', 'exec'), {})
            return json.loads((Path(directory) / 'agent_usage.jsonl').read_bytes())

    def test_canonical_format_uses_explicit_legacy_aic_field(self):
        report = self.canonical('1209.1325', result='failure')
        self.assertEqual(1209.1325, report['aic'])
        self.assertEqual('proxy_total', report['accounting_source'])
        self.assertEqual(123, report['run_id'])
        self.assertEqual(1, report['run_attempt'])

    def test_missing_job_output_is_unknown_and_skipped_agent_is_distinct(self):
        for amount in ('', 'NaN', '-1', '0'):
            with self.subTest(amount=amount):
                report = self.canonical(amount, source='', result='failure')
                self.assertEqual(0, report['aic'])
                self.assertEqual('accounting_unavailable', report['accounting_source'])
        self.assertEqual(0, self.canonical('', source='', result='skipped')['aic'])

    def test_generated_locks_route_daily_cache_through_canonical_record(self):
        root = Path(__file__).resolve().parents[1] / 'workflows'
        for name in ('bugfix-fix.lock.yml', 'bugfix-validate.lock.yml'):
            path = root / name
            lines = path.read_text(encoding='utf-8').splitlines()
            self.assertEqual(lines, insert_accounting(path, lines))
            text = '\n'.join(lines)
            self.assertLess(text.index('name: Publish canonical bugfix usage record'),
                            text.index('name: Collect usage artifact files'))
            self.assertNotIn('name: Write daily AIC usage cache entry', text)
            self.assertIn("BUGFIX_ACCOUNTED_CREDITS: ${{ needs.agent.outputs.aic }}", text)
            self.assertIn('/tmp/gh-aw/usage/agent_usage.jsonl', text)


if __name__ == '__main__':
    unittest.main()
