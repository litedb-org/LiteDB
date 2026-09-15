# Serial approved-issue queue

The queue accepts an explicit ordered list of issue IDs already present in the
immutable runtime's approved manifest. It delegates each campaign to that
runtime's existing orchestrator and integrator. It never discovers additional
issues or dispatches the final full matrix.

Preview the queue using GitHub reads only:

```powershell
python .github/bugfix/serial_queue.py `
  --repo litedb-org/LiteDB `
  --workflow-sha <full-runtime-commit> `
  --workflow-ref <pinned-runtime-branch> `
  --campaign-prefix expansion-01 `
  --issues <first-approved-issue> <second-approved-issue> `
  --dry-run
```

Remove `--dry-run` to execute. Each issue uses the latest integration commit and
accepted-test ledger when its campaign starts. After the campaign reaches
`ready`, the queue runs integration verification, applies the exact tested
candidate, and verifies durable acceptance before continuing. Later preview
items cannot predict the commit produced by an earlier pending fix.

Rerun the same command to resume. Campaign names are `<prefix>-<issue>`; a resumed
campaign retains its original base and runtime. A completed issue is skipped only
when its accepted candidate is an ancestor of the current integration base and
its frozen test identities match the integrated campaign's accepted ledger.
An existing prepared integration resumes only for the same campaign, base and
candidate; the integrator still verifies its complete lock identity and evidence.

A paused, blocked, inconclusive, failed or incompatible campaign stops the queue.
A nonzero child command, moved runtime ref, moved campaign base, mismatched
accepted contract, or another campaign's integration lock also stops it. Resolve
the existing campaign explicitly before retrying; the queue never silently
rebases, changes a runtime, clears a pause, or starts a replacement campaign.

`--max-runs` (default 40) and `--timeout-minutes` (default 180) remain per-campaign
bounds enforced by the existing orchestrator. Follow progress in the child
command output and the controller-owned campaign state.
