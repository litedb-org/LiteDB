// @ts-check
/// <reference types="@actions/github-script" />

const fs = require("fs");
const os = require("os");
const path = require("path");
const { DefaultArtifactClient } = require("./artifact_client.cjs");

const { calculateDailyAICStats, findJSONLFiles, formatAICCredits, sumAICFromUsageJSONLFiles } = require("./daily_aic_workflow_helpers.cjs");
const { parsePositiveCompactNumber } = require("./numeric_limits.cjs");
const { getErrorMessage } = require("./error_helpers.cjs");
const { createRateLimitAwareGithub, fetchAndLogRateLimit } = require("./github_rate_limit_logger.cjs");

const PRIMARY_GUARDRAIL_ARTIFACT_NAMES = ["usage"];
const DAILY_WORKFLOW_WINDOW_MS = 24 * 60 * 60 * 1000;
/** Cache entries older than this threshold (in ms) are skipped when loading. */
const CACHE_RETENTION_MS = 48 * 60 * 60 * 1000;
const MAX_WORKFLOW_RUN_PAGES = 10;
const RATE_LIMIT_RESERVE = 100;
const REQUEST_OVERHEAD_BUDGET = MAX_WORKFLOW_RUN_PAGES + 4;
const ESTIMATED_API_OPERATIONS_PER_RUN = 2;
const INTEGER_FORMATTER = new Intl.NumberFormat("en-US");

/** Path where the per-workflow usage cache is restored by the activation job's cache-restore step. */
const AIC_USAGE_CACHE_FILE_PATH = "/tmp/gh-aw/agentic-workflow-usage-cache.jsonl";

/**
 * @returns {Promise<DefaultArtifactClient>}
 */
async function getArtifactClient() {
  return new DefaultArtifactClient();
}

/**
 * @param {string} message
 * @param {Record<string, unknown>} [details]
 * @returns {string}
 */
function formatDailyGuardrailLogMessage(message, details) {
  if (!details || Object.keys(details).length === 0) {
    return `[daily-workflow-aic] ${message}`;
  }
  let serializedDetails = "";
  try {
    serializedDetails = JSON.stringify(details);
  } catch {
    serializedDetails = JSON.stringify({ error: "failed to serialize log details" });
  }
  return `[daily-workflow-aic] ${message}: ${serializedDetails}`;
}

/**
 * Emit a consistently prefixed daily workflow AI Credits diagnostic log line.
 *
 * @param {string} message
 * @param {Record<string, unknown>} [details]
 * @returns {void}
 */
function logDailyGuardrail(message, details) {
  core.info(formatDailyGuardrailLogMessage(message, details));
}

/**
 * Event types that indicate a user-initiated slash command trigger.
 * When aw_context.event_type is one of these, the workflow was triggered by a user
 * typing a slash command in a comment, and the daily guardrail should not be skipped.
 */
const SLASH_COMMAND_EVENT_TYPES = ["issue_comment", "pull_request_review_comment", "discussion_comment"];
const SLASH_COMMAND_TRIGGERING_EVENTS = ["issues", "issue_comment", "pull_request", "pull_request_review_comment", "discussion", "discussion_comment"];
const LABEL_COMMAND_TRIGGERING_EVENTS = ["issues", "pull_request", "discussion"];

/**
 * @param {string | undefined} value
 * @returns {boolean}
 */
function envFlagEnabled(value) {
  if (typeof value !== "string") {
    return false;
  }
  const normalized = value.trim().toLowerCase();
  return normalized === "true" || normalized === "1" || normalized === "yes";
}

/**
 * @returns {boolean}
 */
function shouldSkipDailyAICGuardrail() {
  const eventName = process.env.GITHUB_EVENT_NAME || "";
  const isWorkflowCall = eventName === "workflow_call";
  const isRepositoryDispatch = eventName === "repository_dispatch";
  const hasSlashCommand = envFlagEnabled(process.env.GH_AW_HAS_SLASH_COMMAND);
  const hasLabelCommand = envFlagEnabled(process.env.GH_AW_HAS_LABEL_COMMAND);
  const rawContext = (process.env.GH_AW_WORKFLOW_DISPATCH_AW_CONTEXT || "").trim();
  const hasDispatchContext = rawContext !== "";
  if (isWorkflowCall || isRepositoryDispatch) {
    return true;
  }
  if (eventName === "workflow_dispatch") {
    // Manual user-triggered runs intentionally bypass the daily guardrail.
    if (!hasDispatchContext) {
      return true;
    }
    // Dispatch-routed slash/label commands intentionally bypass the daily guardrail.
    try {
      const awContext = JSON.parse(rawContext);
      const isLabelCommand = typeof awContext.trigger_label === "string" && awContext.trigger_label.trim() !== "";
      const isSlashCommand = SLASH_COMMAND_EVENT_TYPES.includes(awContext.event_type);
      if (isLabelCommand || isSlashCommand) {
        return true;
      }
    } catch {
      // Malformed aw_context: skip guardrail as a safe fallback for manual dispatch.
    }
    // Existing behavior: dispatch-routed runs with aw_context bypass the guardrail.
    return true;
  }
  if (hasSlashCommand && SLASH_COMMAND_TRIGGERING_EVENTS.includes(eventName)) {
    return true;
  }
  if (hasLabelCommand && LABEL_COMMAND_TRIGGERING_EVENTS.includes(eventName)) {
    return true;
  }
  return false;
}

/**
 * Loads the per-workflow usage cache from the JSONL file restored by the activation job's
 * cache-restore step.  Each line is a JSON object `{ run_id: number, aic: number, timestamp?: string }`.
 *
 * Entries with a `timestamp` older than {@link CACHE_RETENTION_MS} (48 h) are skipped so that
 * stale data cannot inflate the daily-AIC total.  Entries without a `timestamp` (written by an
 * older version of the write script) are kept for backward compatibility.
 *
 * Returns a `Map<runId, aic>` so that callers can check whether a prior run's AIC is already
 * known without downloading the run's artifact from the GitHub API.
 *
 * @param {string} [filePath]
 * @returns {Map<number, number>}
 */
function loadAICUsageCache(filePath) {
  const cachePath = filePath || AIC_USAGE_CACHE_FILE_PATH;
  /** @type {Map<number, number>} */
  const cache = new Map();
  try {
    if (!fs.existsSync(cachePath)) {
      logDailyGuardrail("No usage cache file found; all runs will be resolved via API", { path: cachePath });
      return cache;
    }
    const content = fs.readFileSync(cachePath, "utf8");
    const now = Date.now();
    const cutoff = now - CACHE_RETENTION_MS;
    let loaded = 0;
    let skippedStale = 0;
    for (const rawLine of content.split("\n")) {
      const line = rawLine.trim();
      if (!line || !line.startsWith("{")) {
        continue;
      }
      try {
        const entry = JSON.parse(line);
        // Skip entries that have a timestamp and are older than the retention window.
        if (typeof entry?.timestamp === "string") {
          const ts = Date.parse(entry.timestamp);
          if (Number.isFinite(ts) && ts < cutoff) {
            skippedStale++;
            continue;
          }
        }
        const runId = Number(entry?.run_id);
        const rawAic = entry?.aic;
        const aic = typeof rawAic === "number" ? rawAic : NaN;
        if (Number.isFinite(runId) && runId > 0 && Number.isFinite(aic) && aic >= 0) {
          cache.set(runId, aic);
          loaded++;
        }
      } catch {
        // Ignore malformed lines.
      }
    }
    logDailyGuardrail("Loaded usage cache", { path: cachePath, entriesLoaded: loaded, skippedStale });
  } catch (err) {
    logDailyGuardrail("Failed to load usage cache; proceeding without it", {
      path: cachePath,
      error: typeof err === "object" && err !== null && "message" in err ? String(err.message) : String(err),
    });
  }
  return cache;
}

/**
 * @param {string} artifactName
 * @returns {boolean}
 */
function matchesGuardrailArtifactName(artifactName) {
  if (!artifactName) {
    return false;
  }
  return PRIMARY_GUARDRAIL_ARTIFACT_NAMES.some(name => artifactName === name || artifactName.endsWith(`-${name}`));
}

/**
 * @param {{ listArtifacts: Function, downloadArtifact: Function }} artifactClient
 * @param {number} runId
 * @param {string} token
 * @param {string} owner
 * @param {string} repo
 * @returns {Promise<number>}
 */
async function getRunAIC(artifactClient, runId, token, owner, repo) {
  const { artifacts } = await artifactClient.listArtifacts({
    latest: true,
    findBy: {
      token,
      workflowRunId: runId,
      repositoryOwner: owner,
      repositoryName: repo,
    },
  });
  const artifactSummaries = artifacts.map(item => ({ id: item?.id ?? null, name: item?.name || "" }));
  logDailyGuardrail("Listed workflow artifacts", {
    runId,
    artifactCount: artifacts.length,
    artifacts: artifactSummaries,
  });

  const artifact = artifacts.find(item => item?.name && matchesGuardrailArtifactName(item.name));
  if (!artifact) {
    logDailyGuardrail("No matching guardrail artifact found", {
      runId,
      availableArtifacts: artifactSummaries,
    });
    return 0;
  }
  if (!artifact.id) {
    logDailyGuardrail("Skipping guardrail artifact without an id", {
      runId,
      artifactName: artifact.name,
    });
    return 0;
  }

  logDailyGuardrail("Selected guardrail artifact", {
    runId,
    artifactId: artifact.id,
    artifactName: artifact.name,
  });
  const downloadRoot = fs.mkdtempSync(path.join(os.tmpdir(), `gh-aw-daily-guardrail-${runId}-`));
  const download = await artifactClient.downloadArtifact(artifact.id, {
    path: downloadRoot,
    findBy: {
      token,
      workflowRunId: runId,
      repositoryOwner: owner,
      repositoryName: repo,
    },
  });

  const usageJSONLFiles = findJSONLFiles(download.downloadPath || downloadRoot);
  logDailyGuardrail("Downloaded guardrail artifact", {
    runId,
    artifactId: artifact.id,
    artifactName: artifact.name,
    downloadPath: download.downloadPath || downloadRoot,
    usageJSONLFiles,
  });
  const aic = sumAICFromUsageJSONLFiles(usageJSONLFiles);
  logDailyGuardrail("Computed run AIC from artifact", {
    runId,
    artifactId: artifact.id,
    aic,
  });
  return aic;
}

/**
 * @param {number | undefined} value
 * @returns {string}
 */
function formatInteger(value) {
  const safeValue = typeof value === "number" && Number.isFinite(value) ? Math.round(value) : 0;
  return INTEGER_FORMATTER.format(safeValue);
}

/**
 * @param {string} raw
 * @returns {string}
 */
function escapeMarkdownCell(raw) {
  return String(raw || "")
    .replace(/\|/g, "\\|")
    .replace(/\n/g, " ");
}

/**
 * @param {number} remaining
 * @returns {number}
 */
function computeMaxInspectableRuns(remaining) {
  if (!Number.isFinite(remaining) || remaining <= 0) {
    return 0;
  }
  // Reserve headroom for the workflow-run listing overhead plus a conservative
  // estimate of two API operations per inspected run (artifact lookup and
  // artifact download). Adjust ESTIMATED_API_OPERATIONS_PER_RUN if observed
  // usage changes.
  return Math.max(0, Math.floor((remaining - RATE_LIMIT_RESERVE - REQUEST_OVERHEAD_BUDGET) / ESTIMATED_API_OPERATIONS_PER_RUN));
}

/**
 * @param {any} githubClient
 * @returns {Promise<{remaining:number,limit:number,used:number,reset:string}>}
 */
async function getCoreRateLimitSnapshot(githubClient) {
  const response = await githubClient.rest.rateLimit.get();
  const coreRate = response?.data?.resources?.core || response?.data?.rate || {};
  const reset = coreRate?.reset ? new Date(coreRate.reset * 1000).toISOString() : "";
  return {
    remaining: Number(coreRate?.remaining || 0),
    limit: Number(coreRate?.limit || 0),
    used: Number(coreRate?.used || 0),
    reset,
  };
}

/**
 * @param {string} workflowName
 * @param {string} actorLogin
 * @param {number} threshold
 * @param {Array<{id:number, html_url:string, created_at:string, conclusion:string, aic:number}>} countedRuns
 * @param {{remaining:number,limit:number,used:number,reset:string}} rateLimit
 * @param {{candidateRunsCount:number,inspectedRunsCount:number,truncatedByRateLimit:boolean}} meta
 * @returns {string}
 */
function renderDailyAICSummary(workflowName, actorLogin, threshold, countedRuns, rateLimit, meta) {
  const stats = calculateDailyAICStats(countedRuns);
  const remainingBudget = Math.max(0, threshold - stats.total);
  const usagePercent = threshold > 0 ? ((stats.total / threshold) * 100).toFixed(2) : "0.00";
  const runRows =
    countedRuns.length > 0
      ? countedRuns
          .slice()
          .sort((a, b) => Date.parse(b.created_at || "") - Date.parse(a.created_at || ""))
          .map(run => `| [#${run.id}](${run.html_url || ""}) | ${escapeMarkdownCell(run.created_at || "")} | ${escapeMarkdownCell(run.conclusion || "unknown")} | ${formatAICCredits(run.aic)} |`)
          .join("\n")
      : "| _none_ | — | — | 0 |";

  const noRunData = stats.count === 0;
  const totalAICFormatted = formatAICCredits(stats.total) || "0";
  const avgAICFormatted = noRunData ? "—" : formatAICCredits(stats.average) || "0";
  const stddevAICFormatted = noRunData ? "—" : formatAICCredits(stats.stddev) || "0";
  const minMaxAICFormatted = noRunData ? "— / —" : `${formatAICCredits(stats.min)} / ${formatAICCredits(stats.max)}`;

  const noteLines = [];
  if (meta.truncatedByRateLimit) {
    noteLines.push(`- Stopped early to preserve GitHub API rate limit headroom (${rateLimit.remaining} remaining, reserve ${RATE_LIMIT_RESERVE}).`);
  }
  if (meta.candidateRunsCount > meta.inspectedRunsCount) {
    noteLines.push(`- Considered ${meta.candidateRunsCount} prior runs in the 24h window and inspected ${meta.inspectedRunsCount}.`);
  }
  return [
    `**Workflow:** ${workflowName || "workflow"}`,
    `**Actor:** ${actorLogin || "unknown"}`,
    "",
    "| Statistic | Value |",
    "| --- | ---: |",
    `| 24h total AIC | ${totalAICFormatted} |`,
    `| Threshold | ${formatAICCredits(threshold)} |`,
    `| Threshold used | ${usagePercent}% |`,
    `| Remaining headroom | ${formatAICCredits(remainingBudget) || "0"} |`,
    `| Runs counted | ${formatInteger(stats.count)} |`,
    `| Avg AIC / run | ${avgAICFormatted} |`,
    `| Std dev AIC | ${stddevAICFormatted} |`,
    `| Min / Max AIC | ${minMaxAICFormatted} |`,
    `| API remaining | ${formatInteger(rateLimit.remaining)} / ${formatInteger(rateLimit.limit)} |`,
    `| API used | ${formatInteger(rateLimit.used)} |`,
    `| API reset | ${rateLimit.reset || "unknown"} |`,
    "",
    "Previous runs counted in the last 24 hours:",
    "",
    "| Run | Created | Conclusion | AIC |",
    "| --- | --- | --- | ---: |",
    runRows,
    ...(noteLines.length > 0 ? ["", ...noteLines] : []),
  ].join("\n");
}

/**
 * @param {string} workflowName
 * @param {string} actorLogin
 * @param {number} threshold
 * @param {Array<{id:number, html_url:string, created_at:string, conclusion:string, aic:number}>} countedRuns
 * @param {{remaining:number,limit:number,used:number,reset:string}} rateLimit
 * @param {{candidateRunsCount:number,inspectedRunsCount:number,truncatedByRateLimit:boolean}} meta
 * @returns {Promise<void>}
 */
async function appendDailyAICSummary(workflowName, actorLogin, threshold, countedRuns, rateLimit, meta) {
  const markdown = renderDailyAICSummary(workflowName, actorLogin, threshold, countedRuns, rateLimit, meta);
  core.summary.addDetails("Daily AI Credits Usage (24h)", "\n\n" + markdown);
  await core.summary.write();
}

/**
 * @returns {Promise<void>}
 *
 * Requires github-script globals (`core`, `github`, `context`) provided by setupGlobals().
 *
 * Error handling: all GitHub API interactions after the initial guard checks are wrapped
 * in a top-level try-catch. Any unexpected error (network failure, permission error, etc.)
 * is logged as a warning and the function returns cleanly with `daily_ai_credits_exceeded`
 * left at its default value of `"false"` (safe bypass). When the guardrail is actually exceeded,
 * the step marks the job as failed after setting outputs so downstream conclusion handling can
 * still run and produce failure issues.
 */
async function main() {
  core.setOutput("daily_ai_credits_exceeded", "false");
  core.setOutput("daily_ai_credits_total_effective_tokens", "");
  core.setOutput("daily_ai_credits_threshold", "");
  const threshold = parsePositiveCompactNumber(process.env.GH_AW_MAX_DAILY_AI_CREDITS);
  if (threshold <= 0) {
    return;
  }
  if (shouldSkipDailyAICGuardrail()) {
    core.info("Skipping daily workflow AI Credits guardrail for manual or command-driven runs.");
    return;
  }

  const token = process.env.GH_AW_GITHUB_TOKEN || process.env.GITHUB_TOKEN || process.env.GH_TOKEN || "";
  if (!token) {
    core.warning("Skipping daily workflow AI Credits guardrail because no GitHub token was available for artifact lookup.");
    return;
  }

  // Wrap all GitHub API interactions in a top-level try-catch so that transient API
  // errors, permission failures, or unexpected exceptions never fail the activation
  // job step.  A failure here would leave `daily_ai_credits_exceeded` at its
  // default "false" value, which is the safe fallback: the agent is allowed to run
  // and the guardrail is effectively bypassed for this invocation rather than causing
  // a confusing workflow failure.
  try {
    const githubClient = createRateLimitAwareGithub(github);
    const { owner, repo } = context.repo;
    // Capture a before-guardrail rate-limit snapshot and log it to the JSONL
    // so consumers can determine the baseline available quota before inspection starts.
    const rateLimitStart = await fetchAndLogRateLimit(githubClient, "daily-aic-guardrail-start");
    const currentRun = await githubClient.rest.actions.getWorkflowRun({
      owner,
      repo,
      run_id: context.runId,
    });
    const rateLimit = rateLimitStart ?? (await getCoreRateLimitSnapshot(githubClient));

    const workflowID = process.env.GH_AW_WORKFLOW_ID || "";
    const workflowName = process.env.GH_AW_WORKFLOW_NAME || workflowID || "workflow";
    const actorLogin = process.env.GITHUB_TRIGGERING_ACTOR || currentRun.data.triggering_actor?.login || currentRun.data.actor?.login || process.env.GITHUB_ACTOR || "";

    if (!currentRun.data.workflow_id) {
      core.warning("Skipping daily workflow AI Credits guardrail because the current workflow could not be resolved.");
      return;
    }

    logDailyGuardrail("Resolved current workflow AI Credits guardrail context", {
      owner,
      repo,
      currentRunId: context.runId,
      workflowId: currentRun.data.workflow_id,
      workflowName,
      actorLogin,
      threshold,
      rateLimitRemaining: rateLimit.remaining,
      rateLimitLimit: rateLimit.limit,
    });
    const maxInspectableRuns = computeMaxInspectableRuns(rateLimit.remaining);
    if (maxInspectableRuns <= 0) {
      core.warning(`Skipping daily workflow AI Credits guardrail because the GitHub API rate limit is too low (${rateLimit.remaining} remaining, reserve ${RATE_LIMIT_RESERVE}).`);
      return;
    }

    const cutoffMs = Date.now() - DAILY_WORKFLOW_WINDOW_MS;
    /** @type {Array<{id:number, html_url:string, created_at:string, conclusion:string}>} */
    const candidateRuns = [];
    let page = 1;
    let truncatedByRateLimit = false;
    // listWorkflowRuns returns runs in descending creation order (newest first).
    // The first run whose created_at falls before the cutoff means all remaining
    // runs on this page and every subsequent page are also outside the window, so
    // we can stop paginating immediately rather than exhausting the page budget.
    let reachedCutoff = false;
    while (page <= MAX_WORKFLOW_RUN_PAGES) {
      logDailyGuardrail("Querying completed workflow runs", {
        workflowId: currentRun.data.workflow_id,
        page,
        perPage: 100,
        cutoff: new Date(cutoffMs).toISOString(),
      });
      const response = await githubClient.rest.actions.listWorkflowRuns({
        owner,
        repo,
        workflow_id: currentRun.data.workflow_id,
        status: "completed",
        per_page: 100,
        page,
      });
      const runs = response.data.workflow_runs || [];
      logDailyGuardrail("Received workflow runs page", {
        page,
        runCount: runs.length,
        firstRunId: runs[0]?.id ?? null,
        lastRunId: runs[runs.length - 1]?.id ?? null,
      });
      if (runs.length === 0) {
        break;
      }
      for (const run of runs) {
        if (!run || run.id === context.runId) {
          continue;
        }
        const createdAtMs = Date.parse(run.created_at || "");
        if (!Number.isFinite(createdAtMs) || createdAtMs < cutoffMs) {
          // Runs are newest-first; any run older than the cutoff means all
          // remaining runs (and pages) are also outside the 24h window.
          reachedCutoff = true;
          break;
        }
        candidateRuns.push(run);
        if (candidateRuns.length >= maxInspectableRuns) {
          truncatedByRateLimit = true;
          break;
        }
      }
      if (reachedCutoff || candidateRuns.length >= maxInspectableRuns || runs.length < 100) {
        break;
      }
      page += 1;
    }
    logDailyGuardrail("Prepared candidate workflow runs for artifact inspection", {
      candidateRunsCount: candidateRuns.length,
      candidateRunIds: candidateRuns.map(run => run.id),
      maxInspectableRuns,
      truncatedByRateLimit,
    });

    // Load the per-workflow usage cache restored by the activation job's cache-restore step.
    // Entries that are already cached skip the artifact download entirely, reducing API usage.
    const usageCache = module.exports.loadAICUsageCache();

    const artifactClient = await module.exports.getArtifactClient();
    let totalAIC = 0;
    /** @type {Array<{id:number, html_url:string, created_at:string, conclusion:string, aic:number}>} */
    const countedRuns = [];
    for (const run of candidateRuns) {
      try {
        let runAIC;
        if (usageCache.has(run.id)) {
          // Cache hit: use the previously recorded AIC without downloading the artifact.
          runAIC = usageCache.get(run.id) ?? 0;
          logDailyGuardrail("Cache hit: using cached AIC for run", {
            runId: run.id,
            cachedAIC: runAIC,
          });
        } else {
          // Cache miss: fetch AIC from the run's usage artifact.
          runAIC = await module.exports.getRunAIC(artifactClient, run.id, token, owner, repo);
        }
        if (runAIC <= 0) {
          logDailyGuardrail("Skipping run without AIC usage artifact data", {
            runId: run.id,
            currentAIC: totalAIC,
            threshold,
          });
          continue;
        }
        totalAIC += runAIC;
        countedRuns.push({
          id: run.id,
          html_url: run.html_url || "",
          created_at: run.created_at || "",
          conclusion: run.conclusion || "",
          aic: runAIC,
        });
        logDailyGuardrail("Updated current AIC state", {
          runId: run.id,
          runAIC,
          currentAIC: totalAIC,
          threshold,
          countedRunIds: countedRuns.map(item => item.id),
        });
      } catch (error) {
        core.warning(`Failed to inspect token usage for run ${run.id}: ${getErrorMessage(error)}`);
      }
    }

    core.setOutput("daily_ai_credits_total_effective_tokens", String(totalAIC));
    core.setOutput("daily_ai_credits_threshold", String(threshold));

    /** @type {{candidateRunsCount:number,inspectedRunsCount:number,truncatedByRateLimit:boolean}} */
    const summaryMeta = {
      candidateRunsCount: candidateRuns.length,
      inspectedRunsCount: countedRuns.length,
      truncatedByRateLimit,
    };
    logDailyGuardrail("Completed AIC inspection window", {
      // Keep these explicit to preserve existing log shape (exclude truncatedByRateLimit).
      candidateRunsCount: summaryMeta.candidateRunsCount,
      inspectedRunsCount: summaryMeta.inspectedRunsCount,
      countedRunIds: countedRuns.map(run => run.id),
      currentAIC: totalAIC,
      threshold,
      exceeded: totalAIC > threshold,
    });

    // Capture an after-guardrail rate-limit snapshot and log it to the JSONL so
    // the full cost of the inspection window (workflow-run listing + artifact downloads)
    // can be measured.  The delta between the before and after snapshots answers
    // whether the daily AIC guardrail is too hungry in GitHub API rate limits.
    const rateLimitEnd = await fetchAndLogRateLimit(githubClient, "daily-aic-guardrail-end");
    const rateLimitBeforeInspection = rateLimitStart?.remaining ?? rateLimit.remaining;
    const rateLimitAfterInspection = rateLimitEnd?.remaining ?? rateLimitBeforeInspection;
    logDailyGuardrail("GitHub API rate limit consumed by daily AIC guardrail", {
      rateLimitBeforeInspection,
      rateLimitAfterInspection,
      consumed: Math.max(0, rateLimitBeforeInspection - rateLimitAfterInspection),
      limit: rateLimit.limit,
      reset: rateLimit.reset,
    });

    if (totalAIC <= threshold) {
      await appendDailyAICSummary(workflowName, actorLogin, threshold, countedRuns, rateLimit, summaryMeta);
      core.info(`Daily workflow AIC guardrail not exceeded (${totalAIC}/${threshold}).`);
      return;
    }

    core.setOutput("daily_ai_credits_exceeded", "true");
    try {
      await appendDailyAICSummary(workflowName, actorLogin, threshold, countedRuns, rateLimit, summaryMeta);
    } catch (summaryError) {
      core.warning(`Failed to write daily AIC summary: ${getErrorMessage(summaryError)}`);
    }
    core.warning(`Daily workflow AIC guardrail exceeded for ${workflowName}: ${totalAIC}/${threshold}.`);
    core.setFailed(`Daily workflow AIC guardrail exceeded for ${workflowName}: ${totalAIC}/${threshold}.`);
  } catch (error) {
    // Treat unexpected guardrail execution errors as non-blocking skips so transient
    // API/runtime issues do not fail activation. The output stays at the default "false",
    // allowing the agent to run. Legitimate threshold exceedance still fails via setFailed.
    core.warning(`Daily workflow AI Credits guardrail encountered an unexpected error and will be skipped: ${getErrorMessage(error)}`);
  }
}

module.exports = {
  main,
  getArtifactClient,
  getRunAIC,
  loadAICUsageCache,
  shouldSkipDailyAICGuardrail,
  matchesGuardrailArtifactName,
  findJSONLFiles,
  sumAICFromUsageJSONLFiles,
  calculateDailyAICStats,
  computeMaxInspectableRuns,
  renderDailyAICSummary,
  formatDailyGuardrailLogMessage,
};
