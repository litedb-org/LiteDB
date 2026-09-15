# Hosted sweep ticks

`bugfix-sweep.yml` hosts short invocations of `sweep.py`; no workstation process
is required between ticks. The scheduled, workflow-completion and manual wakeups
are triggers only. Every tick reauthenticates the persisted queue, immutable
runtime, exact campaign, workflow requests and evidence before advancing.

## Commands

Initialize once from an authenticated default-branch hosting run:

```text
python .github/bugfix/sweep.py init --repo litedb-org/LiteDB --sweep canary-v9 --scheduler-sha FULL_CONTROL_SHA --workflow-sha FULL_RUNTIME_SHA --workflow-ref automation/bugfix-runtime-v9 --campaign-prefix hosted-v9 --issues 1002 --owner-run-id HOST_RUN --owner-run-attempt 1
```

Subsequent hosting runs use `tick --repo ... --sweep ... --scheduler-sha ...
--owner-run-id ... --owner-run-attempt ...`. The immutable runtime and issue list
come from controller state, not a changing repository variable. `status` needs
only repository and sweep. `pause`/`resume` also require an auditable `--reason`.
`--dry-run` reads state and pins without taking a lease or dispatching.
Initialization does not dispatch a worker. Supply only reviewed issue IDs (1–40);
the scheduler never discovers or selects arbitrary reports.

## Durability and safety

- `sweep-NAME.json` records immutable scheduler/runtime/source/contract identities,
  ordered issues, dispositions, heartbeat, run IDs, cooldowns and investigation
  references. The hosting workflow Git blob is authenticated from the actual
  Actions run and pinned on initialization. Unrelated default-branch commits
  are allowed; a changed bootstrap workflow or scheduler requires a reviewed
  transition, not a silent variable edit.
- `sweep-lock.json` serializes ticks with exact data-ref compare-and-swap. A live
  Actions owner cannot be displaced by an expired timestamp. A later run may
  reclaim the lock only after the old run completed or its attempt was superseded.
- Each tick invokes the runtime's `orchestrate.py --tick` for one phase. It polls
  existing requests or dispatches ready work and exits without waiting. Reviewer
  roles still dispatch independently; all three verdicts remain required.
- Dispatch intent is saved before the network call. Uncertain responses are
  correlated by the same request ID for ten minutes and never blindly dispatched
  again. If no run can be found, the campaign is blocked with its request journal
  retained for investigation; other independent approved work may advance.
- Candidate creation must use the accompanying deterministic recovery patch.
  Publish and exact remote readback precede journaling the candidate SHA. A
  transport error retries the same worker artifact/candidate; a conflicting
  remote ref blocks. Reviews retain unique event identities across restarts.
- A ready candidate goes through the existing pinned integrator, including
  evidence verification and its expected-base lease. Prepared integration locks
  are never discarded to run another issue. Transport failures use bounded
  backoff; semantic failures are visible and deferred when no integration
  transaction is active. The serial runner remains available.

## Cooldowns and deferred work

Authenticated `bugfix-budget/budget-status.json` reports are the only authority
for budget cooldowns: actual daily-cap activation waits a conservative 24 hours;
`accounting_unavailable` waits 15 minutes. Both require the exact run/attempt/
workflow, a skipped agent and successful trusted conclusion job. Neither spends
candidate or infrastructure retries, and the original maximum 40 workflow
requests still applies. Generic skipped jobs are not budget evidence. Caps and
models are unchanged.

Transport/JSON interruptions and tick timeouts keep the same campaign, with a
global exponential backoff capped at 24 hours. A clean tick resets the consecutive
outage streak. Ordinary workflow infrastructure retries also delay the whole
queue, preventing a provider outage from immediately spending every issue's
retry allowance. Real failed gates, unexpected passes and unresolved review
findings are never converted into acceptance.

Explicit sweep or campaign pauses halt advancement. A blocked campaign is
recorded once with base/candidate/event references, after known child runs drain.
The next independent approved issue starts from the current integration head
and current accepted-test ledger. Issues sharing approved production paths with
a deferred issue are conservatively deferred. At `needs_recovery`, bounded
read-only probes can recognize externally verified acceptance and revisit a
blocked dependency once; they cannot clear a failed campaign themselves.

`accepted_pending_final` means this supplied queue was accepted; the final full
matrix and broader inventory are still pending. `needs_recovery` means deferred
issues remain. No tick dispatches the original full CI matrix.

## Current rollout limitation

The new canary is #1002. The following expansion explicitly excludes the four
already accepted issues (#2874, #2839, #2869, #1506), while their corrected
permanent passing cases remain required on every campaign.

Exact accepted-contract skip checking intentionally rejects the original #2874
archive's historical CP1252 corruption. Its original bytes remain preserved;
the audited correction is at
`evidence/canary-2874-v4/encoding-correction-b530fc144bd2/`, state
`ff3701819065d1682a3147e201a50091a0a1c206`, audit SHA-256
`853b014befe29c0f8d67e3f5a54dbde340bbcc3a236c1ac27d0c0cad69502e5f`.
Supporting that historical skip requires a separate explicit audited adapter;
this scheduler does not normalize archive identities or rewrite old evidence.
