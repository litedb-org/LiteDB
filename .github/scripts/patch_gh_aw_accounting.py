"""Bridge AWF cumulative credits into the pinned gh-aw daily usage format."""

from pathlib import Path


WORKERS = {'bugfix-fix.lock.yml', 'bugfix-validate.lock.yml'}
AGENT_MARKER = '      - name: Account trusted bugfix proxy usage'
CONCLUSION_MARKER = '      - name: Publish canonical bugfix usage record'
ACCOUNT_SCRIPT = '''import json
import math
import os
from pathlib import Path

env = os.environ
path = Path('/tmp/gh-aw/sandbox/firewall/logs/api-proxy-logs/token-usage.jsonl')
observed = 0.0
uncertain = False
try:
    if path.is_symlink() or path.stat().st_size > 64 * 1024 * 1024:
        raise ValueError('Unsafe or oversized accounting file')
    for line in path.read_text(encoding='utf-8').splitlines():
        if not line.strip():
            continue
        try:
            record = json.loads(line)
            if not isinstance(record, dict):
                raise ValueError('Accounting record must be an object')
            if record.get('event') != 'token_usage':
                continue
            value = record.get('ai_credits_total')
            if type(value) not in (int, float) or not math.isfinite(value) or value < 0:
                raise ValueError('Invalid cumulative credit value')
            observed = max(observed, value)
        except (ValueError, TypeError):
            uncertain = True
except (OSError, ValueError):
    uncertain = True
if observed > 0 and not uncertain:
    amount, source = observed, 'proxy_total'
elif observed == 0 and env['BUGFIX_EXECUTION_OUTCOME'] == 'skipped':
    amount, source = 0.0, 'not_started'
else:
    amount, source = observed, 'accounting_unavailable'
with open(env['GITHUB_OUTPUT'], 'a', encoding='utf-8') as output:
    output.write(f'aic={amount}\\nsource={source}\\n')
with open(env['GITHUB_STEP_SUMMARY'], 'a', encoding='utf-8') as summary:
    summary.write(f'\\nBugfix observed accounting: {amount:g} nominal credits ({source}); no credit limit.\\n')
'''

CANONICAL_SCRIPT = '''import json
import math
import os
from pathlib import Path

env = os.environ
source = env['BUGFIX_ACCOUNTING_SOURCE']
try:
    amount = float(env['BUGFIX_ACCOUNTED_CREDITS'])
    if not math.isfinite(amount) or amount < 0:
        raise ValueError('Invalid accounted credits')
except ValueError:
    amount, source = 0.0, 'accounting_unavailable'
if env['BUGFIX_AGENT_RESULT'] == 'skipped':
    amount, source = 0.0, 'not_started'
elif amount == 0 and source != 'not_started':
    amount, source = 0.0, 'accounting_unavailable'
record = {'aic': amount, 'accounting_source': source,
          'run_id': int(env['GITHUB_RUN_ID']), 'run_attempt': int(env['GITHUB_RUN_ATTEMPT'])}
root = Path('/tmp/gh-aw')
root.mkdir(parents=True, exist_ok=True)
path = root / 'agent_usage.jsonl'
if path.is_symlink():
    raise SystemExit('Accounting summary must not be a symlink')
path.write_text(json.dumps(record, sort_keys=True) + '\\n', encoding='utf-8')
'''


def python_lines(script):
    return ["          python3 - <<'PY'", *['          ' + line if line else '' for line in script.splitlines()],
            '          PY']


def steps():
    agent = [AGENT_MARKER, '        id: bugfix-accounting', '        if: always()', '        env:',
             '          BUGFIX_EXECUTION_OUTCOME: ${{ steps.agentic_execution.outcome }}',
             '        run: |', *python_lines(ACCOUNT_SCRIPT)]
    conclusion = [CONCLUSION_MARKER, '        if: always()', '        env:',
                  '          BUGFIX_ACCOUNTED_CREDITS: ${{ needs.agent.outputs.aic }}',
                  '          BUGFIX_ACCOUNTING_SOURCE: ${{ needs.agent.outputs.bugfix_accounting_source }}',
                  '          BUGFIX_AGENT_RESULT: ${{ needs.agent.result }}',
                  '        run: |', *python_lines(CANONICAL_SCRIPT)]
    return agent, conclusion


def insert_accounting(path: Path, lines: list[str]) -> list[str]:
    if path.name not in WORKERS:
        return lines
    agent, conclusion = steps()
    output = "      aic: ${{ steps.bugfix-accounting.outputs.aic }}"
    source = '      bugfix_accounting_source: ${{ steps.bugfix-accounting.outputs.source }}'
    if AGENT_MARKER in lines:
        for block in (agent, conclusion):
            if block[0] not in lines or lines[lines.index(block[0]):lines.index(block[0]) + len(block)] != block:
                raise RuntimeError('Accounting patch drifted; review generated workflow')
        if lines.count(output) != 1 or lines.count(source) != 1:
            raise RuntimeError('Accounting job outputs drifted')
        return lines
    original = '      aic: ${{ steps.parse-mcp-gateway.outputs.aic }}'
    agent_anchor = '      - name: Print AWF reflect summary'
    conclusion_anchor = '      - name: Collect usage artifact files'
    for anchor in (original, agent_anchor, conclusion_anchor, '        id: agentic_execution'):
        if lines.count(anchor) != 1:
            raise RuntimeError('Missing or ambiguous accounting anchor: ' + anchor)
    if not lines.index('  agent:') < lines.index(agent_anchor) < lines.index('  conclusion:') < lines.index(conclusion_anchor):
        raise RuntimeError('Accounting anchors are outside trusted jobs')
    result = []
    for line in lines:
        if line == original:
            result.extend([output, source])
            continue
        if line == agent_anchor:
            result.extend(agent)
        if line == conclusion_anchor:
            result.extend(conclusion)
        result.append(line)
    return result
