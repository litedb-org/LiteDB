# Workflow and reviews

Use this when working with branches, PRs, reviews, and CI.
Before declaring implementation complete, satisfy the
[database-safety completion gate](data-safety.md#database-safety-is-a-completion-gate).
Unresolved safety failures or missing safety tests remain blockers even when CI is green.

## Branches and scope

- Use `gh -R litedb-org/LiteDB` for upstream issues and PRs. The usual remotes
  are `upstream` for `litedb-org/LiteDB` and `origin` for `JKamsker/LiteDB`, but
  inspect `git remote -v` and PR base/head metadata instead of assuming.
- New work normally targets `dev`; preserve explicitly requested older-version
  branches. Fetch the current base and head before reviewing or changing a PR.
- Merge the base into an existing PR branch. Do not rebase or force-push shared
  history unless the user explicitly requests that rewrite. A past exception
  for a particular branch is not standing permission for other branches.
- Keep independent fixes in separate commits, and split unrelated features or
  substantial storage/compatibility changes into focused PRs. Each fix should
  remain independently revertible. When splitting a PR, inspect the resulting
  base-to-head diffs so the fix is not still duplicated in both histories.
- Prefer squash when a merge is authorized. Creating or updating a PR does not
  itself authorize merging it. Keep requested drafts as drafts.

## CI without unnecessary waiting

Continue implementation, review fixes, documentation, and benchmarks while CI
runs. Do not stop after each push to watch checks when useful work remains.
For several PRs, work through the code changes in the requested order, then
monitor the outstanding runs together. This does not imply parallel branch edits.

When the task includes making CI green, finish that work after the final push.
Check the run's commit SHA: a green earlier commit does not verify a later merge
or fix. Diagnose failures from logs; report an unchanged rerun honestly. Do not
call a pending run green or present a local run as hosted-CI evidence.

## Reviews and handoffs

- Read the PR body, linked issue, review summaries, inline threads, and ordinary
  comments. Feedback can arrive on any of these surfaces, including after a push.
- Reproduce retained findings when practical. A bot finding is a hypothesis:
  fix valid ones and explain refuted ones with evidence. Resolve a thread only
  after its concern has been addressed or explicitly refuted.
- If independent review rounds are requested, use fresh reviewers without the
  prior round's conclusions. Honor the requested model and scope; review loops
  are not a standing instruction to spawn agents on every task.
- Write collaborative reviews: state the concrete trigger and user-visible
  consequence, acknowledge what works, and suggest a fix or a discriminating
  test. Explain severity in plain language rather than relying on P1/P2 labels.
- Keep the PR title and description aligned with the final diff. Include current
  validation, relevant before/after measurements, compatibility changes, and
  unresolved questions. Replace stale claims after scope changes.
- For bug sweeps, track each issue's reproduction, fix, tests, and remaining
  scope separately. A related new case need not mean the original fix regressed.
  Search existing issues before treating a finding as new; keep manifests and
  issue notes consistent with the actual test result.

For merge-confidence requests, list the concrete unanswered questions and close
them with targeted evidence. State the remaining limits; a large test count or
several clean reviews is not proof that uncovered paths are safe.
