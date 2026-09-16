"""Replay the exact pinned helper: dispatch admission, cache forks and API failures."""

import json
from pathlib import Path
import subprocess
import tempfile
import unittest

from patch_gh_aw_daily_guard import insert_daily_guard


ROOT = Path(__file__).resolve().parent


class DailyGuardTests(unittest.TestCase):
    def replay(self, **scenario):
        result = subprocess.run(['node', str(ROOT / 'fixtures/replay_daily_guard.cjs'),
                                 json.dumps(scenario)], capture_output=True, text=True, check=True)
        return json.loads(result.stdout)

    def test_parallel_review_cache_fork_recovers_both_missing_runs(self):
        result = self.replay()
        self.assertEqual([12, 13], result['misses'])
        self.assertEqual('600', result['outputs']['daily_ai_credits_total_effective_tokens'])
        self.assertEqual('accounted', result['outputs']['bugfix_budget_status'])
        self.assertFalse(result['failures'])

    def test_budget_reached_blocks_dispatch_at_equal_threshold(self):
        result = self.replay(usage={'12': 2400, '13': 2500})
        self.assertEqual('true', result['outputs']['daily_ai_credits_exceeded'])
        self.assertEqual('accounted', result['outputs']['bugfix_budget_status'])
        self.assertTrue(result['failures'])

    def test_unknown_api_or_partial_artifact_window_defers(self):
        for scenario in ({'apiFailure': True}, {'artifactFailure': True}, {'remaining': 116}):
            with self.subTest(scenario=scenario):
                result = self.replay(**scenario)
                self.assertEqual('accounting_unavailable', result['outputs']['bugfix_budget_status'])
                self.assertEqual('false', result['outputs']['daily_ai_credits_exceeded'])
                self.assertTrue(result['failures'])

    def test_zero_legacy_cache_rechecks_then_reserves_cap(self):
        result = self.replay(cache=[[11, 0]], usage={})
        self.assertEqual([11, 12, 13], result['misses'])
        self.assertEqual('3000', result['outputs']['daily_ai_credits_total_effective_tokens'])

    def test_malformed_history_is_not_an_authenticated_empty_window(self):
        cases = ({'missingRuns': True}, {'invalidRuns': True}, {'nullRecord': True},
                 {'badRecord': {'id': 12}}, {'badRecord': {'id': 0, 'created_at': '2026-09-01T00:00:00Z'}},
                 {'badRecord': {'id': 12, 'created_at': 'invalid'}})
        for scenario in cases:
            with self.subTest(scenario=scenario):
                result = self.replay(**scenario)
                self.assertEqual('accounting_unavailable', result['outputs']['bugfix_budget_status'])
        result = self.replay(runs=[], cache=[])
        self.assertEqual('0', result['outputs']['daily_ai_credits_total_effective_tokens'])
        self.assertEqual('accounted', result['outputs']['bugfix_budget_status'])

    def test_authenticated_skipped_agents_cost_zero(self):
        result = self.replay(cache=[], usage={}, skipped=True)
        self.assertEqual('0', result['outputs']['daily_ai_credits_total_effective_tokens'])
        self.assertEqual('accounted', result['outputs']['bugfix_budget_status'])

    def test_invalid_prior_amount_fails_closed(self):
        result = self.replay(usage={'12': -1})
        self.assertEqual('accounting_unavailable', result['outputs']['bugfix_budget_status'])

    def test_original_artifact_reader_recovers_old_proxy_total_before_reservation(self):
        with tempfile.TemporaryDirectory() as directory:
            file = Path(directory) / 'agent/token_usage.jsonl'
            file.parent.mkdir()
            rows = [{'event': 'token_usage', 'ai_credits_total': n} for n in (100, 900, 1209.1325)]
            file.write_text('\n'.join(json.dumps(row) for row in rows), encoding='utf-8')
            result = self.replay(cache=[[11, 0]], runs=[11], proxyFile=str(file))
            self.assertEqual([11], result['misses'])
            self.assertEqual('1209.1325', result['outputs']['daily_ai_credits_total_effective_tokens'])
            self.assertEqual('accounted', result['outputs']['bugfix_budget_status'])
            result = self.replay(cache=[], runs=[11], proxyFile=str(file), legacySum=100)
            self.assertEqual('1209.1325', result['outputs']['daily_ai_credits_total_effective_tokens'])
            file.write_text('{"event":"token_usage","ai_credits_total":-1}', encoding='utf-8')
            result = self.replay(cache=[], runs=[11], proxyFile=str(file), legacySum=100)
            self.assertEqual('accounting_unavailable', result['outputs']['bugfix_budget_status'])

    def test_current_uncapped_locks_do_not_activate_legacy_guard(self):
        for name in ('bugfix-fix.lock.yml', 'bugfix-validate.lock.yml'):
            lines = (ROOT.parent / 'workflows' / name).read_text(encoding='utf-8').splitlines()
            text = '\n'.join(lines)
            self.assertNotIn('daily-effective-workflow-guardrail', text)
            self.assertNotIn('bugfix_budget_status', text)


if __name__ == '__main__':
    unittest.main()
