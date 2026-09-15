const fs = require('fs');
const vm = require('vm');
const path = require('path');
const adapter = require('../bugfix_daily_guard.cjs');
const scenario = JSON.parse(process.argv[2]);
const source = fs.readFileSync(path.join(__dirname, 'gh_aw_21e402d7_daily_guard.cjs'), 'utf8');
const outputs = {};
const misses = [];
const failures = [];
const core = {
  setOutput: (key, value) => { outputs[key] = String(value); },
  setFailed: text => failures.push(text), info: () => {}, warning: () => {},
  summary: {addRaw() {return this;}, addEOL() {return this;}, async write() {}},
};
const rows = scenario.runs || [11, 12, 13];
const github = {rest: {actions: {
  async getWorkflowRun() {
    if (scenario.apiFailure) throw Error('simulated API failure');
    return {data: {workflow_id: 77}};
  },
  async listWorkflowRuns(options) {
    if (options.workflow_id !== 77 || options.status !== 'completed') throw Error('bad query');
    if (scenario.missingRuns) return {data: {}};
    if (scenario.invalidRuns) return {data: {workflow_runs: {}}};
    if (scenario.badRecord) return {data: {workflow_runs: [scenario.badRecord]}};
    if (scenario.nullRecord) return {data: {workflow_runs: [null]}};
    return {data: {workflow_runs: rows.map(id => ({id, created_at: new Date().toISOString()}))}};
  },
  async listJobsForWorkflowRun() {
    return {data: {total_count: 1, jobs: [{name: 'agent', status: 'completed',
      conclusion: scenario.skipped ? 'skipped' : 'success'}]}};
  },
}}};
const context = {repo: {owner: 'litedb-org', repo: 'LiteDB'}, runId: 99};
const helper = {
  formatAICCredits: String, findJSONLFiles: () => scenario.proxyFile ? [scenario.proxyFile] : [],
  sumAICFromUsageJSONLFiles: () => scenario.legacySum || 0,
  calculateDailyAICStats: rows => ({count: rows.length, total: rows.reduce((s, r) => s + r.aic, 0)}),
};
const dependencies = {
  './artifact_client.cjs': {DefaultArtifactClient: class {}},
  './daily_aic_workflow_helpers.cjs': helper,
  './numeric_limits.cjs': {parsePositiveCompactNumber: Number},
  './error_helpers.cjs': {getErrorMessage: String},
  './github_rate_limit_logger.cjs': {
    createRateLimitAwareGithub: g => g,
    fetchAndLogRateLimit: async () => ({remaining: scenario.remaining || 5000, limit: 5000}),
  },
};
const loaded = {exports: {}};
const sandbox = {module: loaded, core, github, context,
  require: name => dependencies[name] || require(name),
  process: {env: {GH_AW_MAX_DAILY_AI_CREDITS: '5000', GH_AW_GITHUB_TOKEN: 'offline-test',
    GITHUB_EVENT_NAME: 'workflow_dispatch'}},
};
vm.runInNewContext(adapter.patchSource(source), sandbox);
const guard = loaded.exports;
guard.loadAICUsageCache = () => new Map(scenario.cache || [[11, 100]]);
if (scenario.proxyFile) {
  guard.getArtifactClient = async () => ({
    async listArtifacts(options) {
      misses.push(options.findBy.workflowRunId);
      return {artifacts: [{id: 123, name: 'usage'}]};
    },
    async downloadArtifact() {return {downloadPath: path.dirname(scenario.proxyFile)};},
  });
} else {
  guard.getRunAIC = async (_client, runId) => {
    misses.push(runId);
    if (scenario.artifactFailure) throw Error('simulated artifact failure');
    return (scenario.usage || {12: 200, 13: 300})[runId] ?? 0;
  };
}
(async () => {
  await adapter.enforce(guard, core, github, context, 1000);
  process.stdout.write(JSON.stringify({outputs, misses, failures}));
})().catch(error => {throw error;});
