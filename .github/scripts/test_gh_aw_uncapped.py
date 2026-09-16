"""The approved sweep has no implicit or explicit AI-credit admission limit."""

import json
from pathlib import Path
import unittest


WORKFLOWS = Path(__file__).resolve().parents[1] / 'workflows'


def proxy_config(text):
    line = next(line for line in text.splitlines()
                if "printf '%s\\n'" in line and 'awf-config.schema.json' in line)
    return json.loads(line.split("'")[3])['apiProxy']


class UncappedWorkerTests(unittest.TestCase):
    def test_both_workers_explicitly_disable_defaults_and_keep_other_limits(self):
        for name, turns in (('bugfix-fix', 100), ('bugfix-validate', 80)):
            with self.subTest(worker=name):
                source = (WORKFLOWS / f'{name}.md').read_text(encoding='utf-8')
                self.assertIn('\nmax-ai-credits: -1\n', source)
                self.assertIn('\nmax-daily-ai-credits: -1\n', source)
                self.assertIn('\ntimeout-minutes: 30\n', source)
                self.assertIn(f'\nmax-turns: {turns}\n', source)
                text = (WORKFLOWS / f'{name}.lock.yml').read_text(encoding='utf-8')
                proxy = proxy_config(text)
                self.assertNotIn('maxAiCredits', proxy)
                self.assertEqual(turns, proxy['maxRuns'])
                for marker in ('GH_AW_DEFAULT_MAX_AI_CREDITS', 'GH_AW_MAX_DAILY_AI_CREDITS',
                               'daily-effective-workflow-guardrail', 'bugfix-budget',
                               'cap_reservation', 'BUGFIX_CREDIT_CAP',
                               'Write daily AIC usage cache entry'):
                    self.assertNotIn(marker, text)
                credit_env = [line.strip() for line in text.splitlines() if 'GH_AW_MAX_AI_CREDITS:' in line]
                self.assertEqual(['GH_AW_MAX_AI_CREDITS: "-1"'], credit_env)
                self.assertIn('no per-run credit limit; no inherited credit limit', text)
                self.assertIn("'accounting_unavailable'", text)

    def test_readonly_canary_retains_its_credit_cap(self):
        text = (WORKFLOWS / 'bugfix-agent-canary.lock.yml').read_text(encoding='utf-8')
        self.assertEqual(500, proxy_config(text)['maxAiCredits'])
        self.assertIn('A positive AI credit budget is required', text)


if __name__ == '__main__':
    unittest.main()
