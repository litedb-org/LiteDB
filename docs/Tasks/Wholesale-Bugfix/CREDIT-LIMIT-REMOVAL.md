# Remove AI-credit limits from the approved sweep

## Reason and authorization

On September 16, the user explicitly instructed: **"remove the limit entirely"**
after requesting that the failed agent be fixed and verified before work stops.
This supersedes the earlier 50,000 daily AI-credit setting for the approved
bugfix sweep. Both the fixer and validation workers must run without per-worker
or daily AI-credit admission caps. Other workflows retain their own policy.

The old worker did pass daily admission at 13971.885 / 50000, but its separate
2,000-credit execution cap stopped it before it could emit a complete candidate:

| Worker | Result |
| --- | --- |
| [35068448523](https://github.com/litedb-org/LiteDB/actions/runs/35068448523) | Credit cap rejected further requests at 2001.910 / 2000. |
| [35072131341](https://github.com/litedb-org/LiteDB/actions/runs/35072131341) | The unchanged retry failed at 2045.925 / 2000. |

The controller treated the unsuccessful worker as an infrastructure/evidence
retry. Repeating the capped job did not resolve the cause. Neither run produced
an accepted candidate, and no production fix was integrated from either run.

## Rollout requirements

- Preserve the v9 runtime and campaign journals as historical evidence.
- Drain all old requests, including a dispatch whose run ID has not yet been
  written back. Resolve it by exact request ID, workflow, branch and commit.
- Pause the old manifest before activating a new runtime. [Pause run
  35073660331](https://github.com/litedb-org/LiteDB/actions/runs/35073660331)
  completed successfully. The repository dispatch switch was temporarily disabled
  during deployment and reenabled for `hosted-v10-main` after initialization.
- Regenerate the workflows with explicit uncapped behavior. Merely removing a
  repository variable is insufficient because the compiler supplies defaults.
- Keep usage reporting, secret redaction, pinned models and reasoning effort,
  runtime timeouts, restricted patch scope, frozen tests, compressed CI, all
  three independent reviews and exact candidate integration.
- Use a new immutable worker runtime and a reviewed handoff. The same 24 approved
  issues remain in scope. A fresh baseline must verify #1002/#2811 and the 18
  permanent passing cases against the unchanged integration base.
- Restore unattended scheduling and observe a replacement worker completing,
  its candidate being published, and compressed CI accepting that candidate.
  A successful scheduler tick or model startup alone is insufficient proof.

## Reviewed runtime

Runtime [6b9cf0507ae2f341834682da1b1d268946548931](https://github.com/litedb-org/LiteDB/commit/6b9cf0507ae2f341834682da1b1d268946548931)
is pinned on `automation/bugfix-runtime-v10`. Both worker definitions explicitly
set `max-ai-credits: -1` and `max-daily-ai-credits: -1`. Their generated workflows
omit the per-run credit field, cost-based token steering and daily admission
steps. The 50,000 repository variable is no longer referenced by these workers.

The effective runner configuration is checked before inference and reports:
`Bugfix AI credit policy verified: no per-run credit limit; no inherited credit limit`.
Missing usage is labeled `accounting_unavailable`, rather than inventing a cost
or applying a replacement cap. The separate read-only canary remains capped.

Validation: both workflows regenerated successfully; 41 helper tests pass.
Independent review approved the exact runtime tree. Twelve handoff-validator
tests and two independent live read-only previews passed before applying its
audit. Live worker and CI validation are still required.

## Hosted rollout

- [Durable handoff audit](https://github.com/litedb-org/LiteDB/blob/a225d1cdbb784209374d723d6d85751475be9fae/evidence/handoff-hosted-v9-to-v10/audit.json)
  preserves the original journals and failed-run artifact hashes.
- [Initialization 35074889935](https://github.com/litedb-org/LiteDB/actions/runs/35074889935)
  passed for the same 24 approved issues under `hosted-v10-main`.
- [First tick 35074950298](https://github.com/litedb-org/LiteDB/actions/runs/35074950298)
  passed and dispatched [baseline 35075002723](https://github.com/litedb-org/LiteDB/actions/runs/35075002723).

## Evidence to record after worker verification

Record the immutable runtime, handoff audit, new sweep manifest, baseline run,
successful worker, candidate and compressed CI here and in [HANDOFF.md](HANDOFF.md).
Keep [tracking issue #2890](https://github.com/litedb-org/LiteDB/issues/2890)
synchronized with the current deployment and any remaining review findings.
