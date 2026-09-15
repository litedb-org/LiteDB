"""Persist a daily-budget decision from the trusted conclusion job, not the agent."""

from pathlib import Path


WORKERS = {"bugfix-fix.lock.yml", "bugfix-validate.lock.yml"}
MARKER = "      - name: Record trusted bugfix budget deferral"
ANCHOR = "      - name: Handle agent failure"
RUNTIME_SCRIPT = '''import json
import math
import os
import re
from datetime import datetime, timezone
from pathlib import Path

env = os.environ
if not re.fullmatch(r'[0-9a-f]{40}', env['BUDGET_WORKFLOW_SHA']):
    raise SystemExit('Budget evidence requires the immutable workflow SHA')
if not all(re.fullmatch(r'[1-9][0-9]*', env[key]) for key in ('GITHUB_RUN_ID', 'GITHUB_RUN_ATTEMPT')):
    raise SystemExit('Budget evidence requires a positive run ID and attempt')
report = {'schema_version': 1, 'run_id': int(env['GITHUB_RUN_ID']),
          'run_attempt': int(env['GITHUB_RUN_ATTEMPT']), 'workflow_sha': env['BUDGET_WORKFLOW_SHA'],
          'observed_at': datetime.now(timezone.utc).isoformat(timespec='seconds').replace('+00:00', 'Z')}
if env.get('BUDGET_STATUS') == 'accounting_unavailable':
    if env['BUDGET_EXCEEDED'] == 'true':
        raise SystemExit('Ambiguous budget disposition')
    report['status'] = 'accounting_unavailable'
else:
    if env['BUDGET_EXCEEDED'] != 'true':
        raise SystemExit('Budget deferral requires an explicit exceeded decision')
    threshold = float(env['BUDGET_THRESHOLD'])
    total = float(env['BUDGET_TOTAL'])
    if not math.isfinite(threshold) or threshold <= 0 or not math.isfinite(total) or total < threshold:
        raise SystemExit('Invalid daily budget accounting evidence')
    report.update(exceeded=True, threshold=threshold, total=total)
root = Path(env['RUNNER_TEMP']) / 'bugfix-budget'
root.mkdir(parents=True, exist_ok=False)
(root / 'budget-status.json').write_text(json.dumps(report, sort_keys=True) + '\\n', encoding='utf-8')
'''


def evidence_steps():
    return [
        MARKER,
        "        id: bugfix-budget",
        "        if: always() && (needs.activation.outputs.daily_ai_credits_exceeded == 'true' || needs.activation.outputs.bugfix_budget_status == 'accounting_unavailable')",
        "        env:",
        "          BUDGET_STATUS: ${{ needs.activation.outputs.bugfix_budget_status }}",
        "          BUDGET_EXCEEDED: ${{ needs.activation.outputs.daily_ai_credits_exceeded }}",
        "          BUDGET_THRESHOLD: ${{ needs.activation.outputs.daily_ai_credits_threshold }}",
        "          BUDGET_TOTAL: ${{ needs.activation.outputs.daily_ai_credits_total_effective_tokens }}",
        "          BUDGET_WORKFLOW_SHA: ${{ github.workflow_sha }}",
        "        run: |",
        "          python3 - <<'PY'",
        *["          " + line if line else "" for line in RUNTIME_SCRIPT.splitlines()],
        "          PY",
        "      - name: Upload trusted bugfix budget deferral",
        "        if: always() && steps.bugfix-budget.outcome == 'success'",
        "        uses: actions/upload-artifact@ea165f8d65b6e75b540449e92b4886f43607fa02 # v4",
        "        with:",
        "          name: bugfix-budget",
        "          path: ${{ runner.temp }}/bugfix-budget/budget-status.json",
        "          if-no-files-found: error",
        "          retention-days: 90",
    ]


def insert_budget_evidence(path: Path, lines: list[str]) -> list[str]:
    if path.name not in WORKERS:
        return lines
    block = evidence_steps()
    if MARKER in lines:
        start = lines.index(MARKER)
        if lines[start:start + len(block)] != block:
            raise RuntimeError('Existing budget evidence patch differs; review compiler output')
        return lines
    if lines.count('  conclusion:') != 1 or lines.count(ANCHOR) != 1:
        raise RuntimeError('Missing or ambiguous trusted conclusion job')
    conclusion = lines.index('  conclusion:')
    insertion = lines.index(ANCHOR)
    if insertion <= conclusion or any(line.startswith('  ') and not line.startswith('   ')
                                      and line.strip() for line in lines[conclusion + 1:insertion]):
        raise RuntimeError('Budget evidence anchor is outside the conclusion job')
    for output in ('daily_ai_credits_exceeded', 'daily_ai_credits_threshold', 'daily_ai_credits_total_effective_tokens'):
        if not any(line.startswith('      ' + output + ':') for line in lines[:conclusion]):
            raise RuntimeError('Required activation budget output is missing: ' + output)
    return lines[:insertion] + block + lines[insertion:]
