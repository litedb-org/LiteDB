"""Make the pinned framework's daily admission guard effective for bugfix dispatches."""

from pathlib import Path

# Retained only for replaying the former capped runtime. Current compilation
# deliberately does not invoke this adapter after the user disabled credit caps.
CAPS = {'bugfix-fix.lock.yml': 2000, 'bugfix-validate.lock.yml': 1000}


MARKER = '            // Enforce daily accounting for dispatched bugfix workers.'
OUTPUT = '      bugfix_budget_status: ${{ steps.daily-effective-workflow-guardrail.outputs.bugfix_budget_status }}'


def insert_daily_guard(path: Path, lines: list[str]) -> list[str]:
    if path.name not in CAPS:
        return lines
    script = Path(__file__).with_name('bugfix_daily_guard.cjs').read_text(encoding='utf-8')
    script = script.replace('module.exports = { patchSource, recoverProxyCredits, enforce, run };', '')
    block = [MARKER, *['            ' + line if line else '' for line in script.splitlines()],
             "            await run('${{ runner.temp }}/gh-aw/actions/check_daily_aic_workflow_guardrail.cjs',",
             f'              core, github, context, {CAPS[path.name]});']
    if MARKER in lines:
        start = lines.index(MARKER)
        if lines.count(OUTPUT) != 1 or lines[start:start + len(block)] != block:
            raise RuntimeError('Patched daily guard changed; recompile and review')
        return lines
    require = "            const { main } = require('${{ runner.temp }}/gh-aw/actions/check_daily_aic_workflow_guardrail.cjs');"
    if lines.count(require) != 1 or lines[lines.index(require) + 1] != '            await main();':
        raise RuntimeError('Daily guard call changed')
    start = lines.index(require)
    lines = lines[:start] + block + lines[start + 2:]
    activation = next((i for i, line in enumerate(lines) if line.startswith('      daily_ai_credits_exceeded:')), None)
    if activation is None:
        raise RuntimeError('Missing activation budget outputs')
    lines.insert(activation, OUTPUT)
    original = "      needs.activation.outputs.stale_lock_file_failed == 'true' || needs.activation.outputs.daily_ai_credits_exceeded == 'true')"
    if lines.count(original) != 1:
        raise RuntimeError('Conclusion admission changed')
    lines[lines.index(original)] = original[:-1] + " || needs.activation.outputs.bugfix_budget_status == 'accounting_unavailable')"
    return lines
