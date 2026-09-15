# Hosted unattended sweep

The hosted scheduler resumes durable work in short GitHub Actions runs. It needs
no local process, open chat, or machine left running. Each tick polls, dispatches,
or consumes one bounded stage; it does not wait for an agent or CI to finish.
The scheduler preserves existing per-fix CI, three independent reviews, exact
candidate integration, retry limits, and blocked-issue dispositions. It never
launches the original full matrix for each fix.

## Deployment boundary

Only `.github/workflows/bugfix-sweep.yml` must be added to the default branch,
currently `dev`. The Python controller, worker workflows, tests, manifests and
library changes remain on separately reviewed immutable automation commits.
The bootstrap validates `BUGFIX_SCHEDULER_SHA` as a full commit SHA before checking
out any controller code, verifies checkout HEAD, and passes that exact SHA to
every mutating controller operation. Initialization records this scheduler pin;
subsequent operations reject a changed pin. A completed child workflow is only a wakeup:
its checkout, artifacts and input values are never executed by the bootstrap.
The controller independently authenticates recorded run identities and artifacts.

The current `dev` prerelease workflow also triggers on workflow-file pushes.
Deploy the narrow bootstrap commit with `[skip ci]` in its commit message to avoid
an unrelated release/full-matrix run. This skips push/pull-request workflows;
the explicit dispatch and scheduled scheduler remain available. Verify the actual
Actions results after deploying. Do not merge the entire automation branch into
`dev` just to enable the scheduler.

The runtime pin must contain `sweep.py`, its controller dependencies, and
`sweep_report.py`. The worker/check runtime selected during initialization must
support bounded `--tick` execution. Do not point these variables at older v8 code.
Changing the repository variable does not authorize an existing sweep to adopt a
new scheduler. Use an explicitly reviewed handoff or initialize a new sweep after
draining the old one; preserve all campaign and worker/runtime identities.

## Repository settings

| Variable | Value and purpose |
| --- | --- |
| `BUGFIX_SCHEDULER_SHA` | Full reviewed immutable commit containing the hosted controller and status publisher. |
| `BUGFIX_ACTIVE_SWEEP` | Durable sweep name, such as `hosted-v9`. |
| `BUGFIX_SWEEP_ENABLED` | Exact string `true` enables scheduled, completion-triggered and manual ticks. Any other value disables ticks. |

The job requests `contents: write` for state/candidate/integration branches,
`actions: write` for explicit child dispatch, and `issues: write` solely for the
fixed status comment on tracking issue #2890. It does not request pull-request
permissions. Only `GITHUB_TOKEN` is supplied to the controller and publisher.
Worker secrets `OPENAI_API_KEY` and `CODEX_LB_BASE_URL` stay in the worker jobs.
No additional PAT is required: GitHub expressly allows `workflow_dispatch` events
created with `GITHUB_TOKEN` to launch workflows. Token-generated branch pushes
do not supply the wakeup mechanism. See [GitHub trigger rules](https://docs.github.com/en/actions/how-tos/write-workflows/choose-when-workflows-run/trigger-a-workflow).

## Initialize, enable, pause

Set the scheduler SHA and active sweep variables while leaving the enable switch
false. Use the reviewed immutable worker runtime SHA/ref and explicit approved
issue list; initialization persists configuration and does not dispatch workers.

```sh
gh variable set BUGFIX_SCHEDULER_SHA -R litedb-org/LiteDB --body "$SCHEDULER_SHA"
gh variable set BUGFIX_ACTIVE_SWEEP -R litedb-org/LiteDB --body hosted-v9
gh variable set BUGFIX_SWEEP_ENABLED -R litedb-org/LiteDB --body false
gh workflow run bugfix-sweep.yml -R litedb-org/LiteDB --ref dev \
  -f operation=init -f sweep=hosted-v9 -f workflow_sha="$WORKER_SHA" \
  -f workflow_ref=automation/bugfix-runtime-v9 -f campaign_prefix=hosted-v9 \
  -f issues='2802 2770'
```

Inspect the initialized state and successful hosted run before enabling. The
example issues are placeholders for the explicitly approved rollout list, not
authorization to bypass an existing blocked campaign. The current #1002 failed
history must remain intact with its unresolved overlap disposition visible.

```sh
gh variable set BUGFIX_SWEEP_ENABLED -R litedb-org/LiteDB --body true
gh workflow run bugfix-sweep.yml -R litedb-org/LiteDB --ref dev -f operation=tick
gh workflow run bugfix-sweep.yml -R litedb-org/LiteDB --ref dev \
  -f operation=pause -f reason='Operator requested durable pause'
```

For an immediate stop to future ticks, set the enable variable false as well.
This does not cancel an already running tick or child worker. A durable pause
preserves all pending run IDs and evidence. Resume explicitly with
`operation=resume` and a reason, then re-enable ticks. Resume does not clear a
blocked correctness disposition or grant new repair attempts. `operation=status`
reads current state and refreshes the tracking comment even when ticks are off.

## Wakeups and recovery

- A five-minute schedule offset from the hour provides recurring recovery.
- Completion of the named fix, validation or check workflow requests an earlier
  tick. The wakeup must originate from an explicit dispatch in this repository
  on an `automation/bugfix-runtime-*` branch.
- All operations share one concurrency group with cancellation disabled. The
  controller additionally uses a durable compare-and-swap lease bound to the
  owning Actions run and attempt. A new tick must not steal a live owner's lock.
- Each controller invocation has an eight-minute subprocess limit and the job
  has a ten-minute limit. Interruption leaves durable requests for the next tick
  to reconcile; it must not blindly dispatch a second copy.
- Blocked/deferred items remain visible in the durable sweep disposition and
  tracking comment. They must not cause a new model attempt every five minutes.
  Existing per-run and daily credit limits remain enabled.
- Do not run a local queue, the old long-running controller, or another publisher
  concurrently with the hosted sweep. Workflow concurrency does not serialize
  unrelated scripts; controller state leases remain the final conflict check.

GitHub schedules are best-effort and can be delayed or dropped. `workflow_run`
chains have a depth limit, so completion wakeups are an optimization and cron is
the independent fallback. Public-repository schedules can be disabled after
60 days without repository activity. This is unattended hosted execution, not a
guaranteed five-minute SLA. See [GitHub event limits](https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows).

## Hosted canary before expansion

1. Review controller/worker commits and deploy only the bootstrap to `dev`.
2. Initialize a one-issue sweep, enable it, and dispatch one tick on GitHub.
3. Verify the automatic token can persist state, dispatch an approved worker,
   publish the restricted production candidate, run compressed CI and update
   tracking issue #2890. Worker runtime/model proofs must still pass.
4. Confirm a subsequent scheduled or completion-triggered run advances the same
   durable journal with no local process. Exercise pause/resume at a stage boundary.
5. Confirm global lock recovery and that a blocked item records one disposition
   instead of consuming repeated model runs. Expand the approved list only after
   the hosted cycle is verified.

Run bootstrap tests with `python .github/scripts/test_hosted_sweep_workflow.py`
(test dependency: PyYAML). These validate event gates, the immutable checkout,
literal argument passing, token scope, time limits, and status publication guard.
