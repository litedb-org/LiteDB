// Maintained adapter for gh-aw 21e402d7: dispatched bugfix workers must be budgeted.
// No model/provider credentials are used. The original source is replayed in tests.
function patchSource(source) {
  const expected = '3f9013365811519fe831ceb46634a9dae2bc77d80a53e62985a5f90de0a0eead';
  const actual = require('crypto').createHash('sha256').update(source).digest('hex');
  if (actual !== expected) throw new Error('Pinned daily guard source changed');
  function replaceOnce(before, after) {
    if (source.split(before).length !== 2) throw new Error('Daily guard anchor changed');
    source = source.replace(before, after);
  }
  replaceOnce('function shouldSkipDailyAICGuardrail() {',
    'function shouldSkipDailyAICGuardrail() {\n  return false; // Bugfix dispatches are budgeted.');
  replaceOnce('    if (!currentRun.data.workflow_id) {',
    '    if (!Number.isInteger(currentRun.data.workflow_id) || currentRun.data.workflow_id <= 0) {');
  replaceOnce('      const runs = response.data.workflow_runs || [];',
    '      const runs = response.data.workflow_runs;\n' +
    '      if (!Array.isArray(runs)) throw new Error("Invalid workflow history response");');
  replaceOnce('        if (!run || run.id === context.runId) {',
    '        if (!run || !Number.isInteger(run.id) || run.id <= 0 || !Number.isFinite(Date.parse(run.created_at || "")))\n' +
    '          throw new Error("Invalid workflow history record");\n' +
    '        if (run.id === context.runId) {');
  replaceOnce('core.warning(`Failed to inspect token usage for run ${run.id}: ${getErrorMessage(error)}`);',
    'throw error; // Partial accounting must not admit another worker.');
  replaceOnce('  const aic = sumAICFromUsageJSONLFiles(usageJSONLFiles);',
    '  const legacy = sumAICFromUsageJSONLFiles(usageJSONLFiles);\n' +
    '  const proxy = module.exports.recoverProxyCredits(usageJSONLFiles);\n' +
    '  if (typeof legacy !== "number" || !Number.isFinite(legacy) || legacy < 0) throw new Error("Invalid legacy credits");\n' +
    '  const aic = Math.max(legacy, proxy);');
  replaceOnce('    core.setOutput("daily_ai_credits_total_effective_tokens", String(totalAIC));',
    '    if (truncatedByRateLimit || page > MAX_WORKFLOW_RUN_PAGES) throw new Error("Incomplete accounting window");\n' +
    '    core.setOutput("daily_ai_credits_total_effective_tokens", String(totalAIC));');
  return source;
}

function recoverProxyCredits(files) {
  const fs = require('fs');
  const candidates = files.filter(file => file.replaceAll('\\', '/').endsWith('/agent/token_usage.jsonl'));
  if (!candidates.length) return 0;
  if (candidates.length !== 1) throw new Error('Ambiguous historic proxy accounting');
  const file = candidates[0];
  const stat = fs.lstatSync(file);
  if (!stat.isFile() || stat.isSymbolicLink() || stat.size > 64 * 1024 * 1024)
    throw new Error('Unsafe historic proxy accounting');
  let amount = 0;
  for (const line of fs.readFileSync(file, 'utf8').split('\n').filter(line => line.trim())) {
    const row = JSON.parse(line);
    if (row.event !== 'token_usage') continue;
    const value = row.ai_credits_total;
    if (typeof value !== 'number' || !Number.isFinite(value) || value < 0)
      throw new Error('Invalid historic proxy accounting');
    amount = Math.max(amount, value);
  }
  return amount;
}

async function enforce(guard, core, github, context, cap) {
  const outputs = {};
  const originalOutput = core.setOutput;
  core.setOutput = (name, value) => {
    outputs[name] = String(value);
    originalOutput(name, value);
  };
  try {
    if (!Number.isFinite(cap) || cap <= 0) throw new Error('Invalid worker cap');
    guard.recoverProxyCredits = recoverProxyCredits;
    const originalCache = guard.loadAICUsageCache;
    guard.loadAICUsageCache = () => new Map([...originalCache()].filter(
      ([, value]) => typeof value === 'number' && Number.isFinite(value) && value > 0));
    const originalUsage = guard.getRunAIC;
    guard.getRunAIC = async (...args) => {
      const value = await originalUsage(...args);
      if (typeof value !== 'number' || !Number.isFinite(value) || value < 0)
        throw new Error('Invalid prior usage');
      if (value > 0) return value;
      // Old aliases produced zero-valued cache/artifact history. Reserve the cap
      // unless authenticated jobs establish that the agent never started.
      const [, runId, , owner, repo] = args;
      const response = await github.rest.actions.listJobsForWorkflowRun({
        owner, repo, run_id: runId, per_page: 100, filter: 'all',
      });
      const jobs = response.data.jobs;
      if (!Array.isArray(jobs) || !Number.isInteger(response.data.total_count) ||
          response.data.total_count !== jobs.length)
        throw new Error('Incomplete historical job evidence');
      const agents = jobs.filter(job => job.name === 'agent');
      if (agents.length && agents.every(job => job.status === 'completed' && job.conclusion === 'skipped'))
        return 0;
      return cap;
    };
    await guard.main();
    const rawTotal = outputs.daily_ai_credits_total_effective_tokens;
    const rawThreshold = outputs.daily_ai_credits_threshold;
    const total = Number(rawTotal);
    const threshold = Number(rawThreshold);
    if (!rawTotal || !rawThreshold || !Number.isFinite(total) || total < 0 ||
        !Number.isFinite(threshold) || threshold <= 0)
      throw new Error('Daily usage could not be established');
    if (total >= threshold) {
      core.setOutput('daily_ai_credits_exceeded', 'true');
      core.setFailed('Bugfix daily admission threshold reached; automatic cooldown required.');
    }
    core.setOutput('bugfix_budget_status', 'accounted');
  } catch {
    // Do not print exception text: provider/API failures need only a recoverable
    // machine-readable disposition, never credentials or arbitrary remote text.
    core.setOutput('bugfix_budget_status', 'accounting_unavailable');
    core.setOutput('daily_ai_credits_exceeded', 'false');
    core.setFailed('Bugfix daily accounting unavailable; automatic cooldown required.');
  } finally {
    core.setOutput = originalOutput;
  }
}

async function run(filename, core, github, context, cap) {
  try {
    const source = patchSource(require('fs').readFileSync(filename, 'utf8'));
    const Module = require('module');
    const loaded = new Module(filename);
    loaded.filename = filename;
    loaded.paths = Module._nodeModulePaths(require('path').dirname(filename));
    loaded._compile(source, filename);
    await enforce(loaded.exports, core, github, context, cap);
  } catch {
    core.setOutput('bugfix_budget_status', 'accounting_unavailable');
    core.setOutput('daily_ai_credits_exceeded', 'false');
    core.setFailed('Bugfix daily accounting setup unavailable; automatic cooldown required.');
  }
}

module.exports = { patchSource, recoverProxyCredits, enforce, run };
