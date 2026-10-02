# Safety evidence and coverage accounting

Use this when opening or reviewing a PR, removing or weakening tests, adding
fault hooks, or changing CI. It makes the [data-safety gate](data-safety.md) and
[validation rules](validation.md) checkable (#3034). The question every change
answers is: **which previously valid states, transitions, callers and files can
this invalidate, and what evidence would detect that?**

The checks below verify that evidence exists, is current and actually ran. They
cannot judge whether prose is true, and a green run is bounded evidence, not
proof of safety.

## The PR section

Every PR description has the `## Safety / regression evidence` section from the
[template](../../.github/pull_request_template.md). `gh pr create --body` ignores
the template, so copy the section yourself. Answer each of the five prompts in a
line or two and link tests, runs and reports instead of pasting logs. A change
that cannot affect any previously supported behavior (documentation only) may
replace the prompts with one `- Not applicable: <reason>` line.

The **Safety section** workflow checks the section and requires every contract
id that the changed paths implicate. The **Safety policy** CI job lists those
ids. Name each id with its evidence, or say why the change cannot affect it. The
path mapping only suggests obligations. A transitive storage or concurrency
effect still counts, and changing a durability oracle, a test filter, a skip, a
timeout or a corpus expectation is not low risk because only tests changed.
Scale the evidence with the affected surface as described in
[validation](validation.md#scale-evidence-with-complexity-and-persistence-risk).

Say which failure model a test covers instead of writing "crash-safe":

| Model | The evidence must show |
| --- | --- |
| `exception` | Cleanup ran; transient versus continued failure during rollback or recovery |
| `process-death` | No graceful disposal; surviving OS cache, processes and locks; cold re-entry |
| `modeled-power-loss` | Only durable bytes survive: lost, torn or reordered unsynced writes and the directory entries the protocol relies on |
| `device` | An actual OS/VM reset or device campaign, never inferred from a process kill |
| `corrupt-input` | Detection and diagnostics, no silent empty or stale result, source preserved |

Oracles classify each transaction as durably acknowledged (its effect must
survive), known aborted (no partial effect), outcome unknown at the interruption
(only complete protocol-permitted outcomes), or explicitly degraded (the
documented weaker guarantee and its diagnostics). "Fails closed" passes only
where the contract permits a refusal: a supported file must still open, migrate
or request its documented upgrade.

## Contracts and fault points

[`contracts.json`](../../.github/safety/contracts.json) indexes a few high-value
contracts. Each entry links to the normative documents, the paths that
implicate it, and evidence items. Each evidence item is a test, fuzz target or
compatibility script, with the failure model and the observed event it `proves`
(a hit list, two live snapshots, an index plan, a real handoff), plus the known
gaps. Every reference must resolve. Fuzz targets must run in `fuzz.yml`, and
scripts must run in a workflow, so a rename or an unscheduled target cannot leave
a stale claim. The report's model matrix shows unclaimed models; a `-` is a gap,
not a pass.

[`fault-points.json`](../../.github/safety/fault-points.json) registers every
string-named fault hook in `LiteDB/`. Hooks come in four families:
`CrashPoint`/`SimulateProcessCrash`, `SimulateInstallFailure`,
`CheckpointStage` and coordination-file stages. Every delegate field under
`#if DEBUG || TESTING` must be registered as one of these families, a data-driven
injector, or an observer with a reason. A new hook, a removed hook or a
non-literal hook name fails the Safety policy job. Evidence for a hook is a test
or fuzz target whose own code names it: the referenced method, a shared member
of its file (MemberData, constants), or a declared `via` helper. A hook without
evidence states its gap. Registered-hook coverage is not proof that the fault
model is complete. New persistent I/O (`FlushToDisk`, `SetLength`, renames and
deletes) is flagged for review, and `unhookedTransitions` records known
boundaries that have no hook, such as #3011.

Changes to the safety machinery itself (oracles, markers, registries, corpus
expectations, test settings, scripts and workflows) implicate the
`safety-machinery` contract, so the PR section must name its evidence like any
other risky change.

[`markers.json`](../../.github/safety/markers.json) registers every reachability
marker (TESTING-only `Reachability.Sometimes`): maintenance interleavings,
documented refusals, situations of fuzz targets and `api:` markers. Fault-point
markers are derived from `fault-points.json`, so every hook site also counts its
marker. The Safety policy job runs `check_reachability.py`: an unregistered,
non-literal or stale marker fails, and so does a public member added or changed
on the `ILite*` interfaces (or a new public type) without a registered `api:`
marker. The Fuzz workflow's Reachability gate then fails a PR whose smoke campaign
never hit a marker its diff declares, unless the entry is `advisory` with a reason.
A marker hit count is reachability evidence, not proof that an assertion ran there.

## Coverage accounting

Coverage may change, but never silently. The Safety policy job compares the
change with its base and flags:

- removed tests, new skips and conditional attributes;
- fewer assertion calls in a test file;
- changed or deleted fixtures and compatibility harnesses;
- fuzz corpus repins, removed cases and new expected-failure entries;
- removed or unscheduled fuzz targets;
- evidence removed from `contracts.json` or `fault-points.json`;
- changed test settings, CI filters, partitions, guards, timeouts and
  `continue-on-error`.

Each finding needs a disposition in
[`coverage-ledger.json`](../../.github/safety/coverage-ledger.json) **added by
the same change**. An entry from an earlier PR never approves a later weakening.
A disposition names the subjects, the invariant the old coverage protected and
the reason, and uses one of these values:

- `replaced`, `relocated` or `covered-elsewhere`: `coveredBy` references are
  required and must resolve.
- `intentional-change`: requires a `decision` link to the compatibility decision.
  An updated golden file alone is not one.
- `strengthened` or `obsolete`: the reason is enough.

A new test that proves one property does not justify losing a separate property
such as sustained-contention progress.

The ledger's `quarantine` lists tests no CI leg executes, each with an owner, a
reason, a review date and the gap it leaves. A quarantined test is a visible
coverage gap, never passing evidence. A passed review date is reported on every
run.

## Diff nets

These nets judge the change against its base. Each one answers a question a diff
reviewer cannot answer reliably. None of them proves the code correct.

| Net | Catches | Runs |
| --- | --- | --- |
| [Diff lints](#diff-lints) | new polling loops, deleted invariant comments, unanchored doc claims | every PR (Safety policy, Safety section) |
| [Differential run](#differential-run) | behavior changes nobody declared, and declared changes that did not happen | PRs touching `LiteDB/` |
| [Mutation on the diff](#mutation-on-the-diff) | changed cleanup and lock lines that no test pins down | PRs labelled `critical`, manual |
| [Contention benchmark](performance.md#contended-acquire) | acquire-latency tails and starvation | the shared-slot performance workflow |

### Diff lints

The Safety policy job runs three lints on the lines a PR adds or removes:

- `lint_polling.py`: a timed wait (`WaitOne(n)`, `Monitor.Wait(x, n)`,
  `SemaphoreSlim`/`ManualResetEventSlim.Wait(n)`, `WaitAny(.., n)`), a sleep,
  `Task.Delay` or a spin added inside a loop in `LiteDB/` needs
  `// polling: <reason>` on its line or within the two lines above. A waiter that
  times out and retries loses its place in the queue, so later arrivals can win
  the handoff and tail latency grows with the interval.
  [test: .github/scripts/test_lint_polling.py#test_timed_wait_in_loop_condition_fires]
- `lint_invariant_comments.py`: a deleted comment in `LiteDB/` stating an
  ordering or prohibition (`must not`, `never`, `invariant`, `do not`,
  `must ... before/after`, `cannot`, `without`) must be listed in the PR
  description, with where the invariant is enforced now:

  ```markdown
  ### Moved invariants

  - LiteDB/Client/Shared/X.cs:120 → LiteDB.Tests/Shared/X_Tests.cs#Waiters_Keep_Their_Turn
  - "readers must not outlive the pin" → [marker: refusal:pin-closed]
  ```

  Name the base location or quote three or more words of the comment; the
  reference after the arrow must resolve at head. A comment moved or reflowed
  unchanged is no deletion. The Safety section workflow checks the description;
  the Safety policy job only lists the deletions.
- `lint_doc_claims.py`: a new or changed sentence under `docs/` containing
  `propagate`, `never`, `always`, `rejects`, `refuses` or `guaranteed` carries
  `[test: <path-or-Class>#<Method>]` or `[marker: <name>]` on the same sentence,
  and the anchor must resolve (a marker against `markers.json`, a test method in
  a test project or a `.github/scripts/test_*.py` unittest). Unchanged sentences
  are not judged; `--all` lists the backlog without failing.
  [test: .github/scripts/test_lint_doc_claims.py#test_new_claim_without_anchor_fails]

An anchor shows that evidence exists for the claim. It does not show that the
claim is true; the differential run and the oracles judge behavior.

### Differential run

`differential_run.py` builds the merge-base and the head in separate worktrees
(`TestingEnabled=true`), runs the same fuzz targets, seeds and counts on each
tree's own harness, and compares the run directories' `outcomes.jsonl`,
`closed-clean.jsonl` and `markers.json` per operation class and dimension:

- escaped exception types (with error code) and outcome kinds, which fail when
  they differ;
- p50/p99 latency, which is advisory beyond +50 % and 5 ms and fails beyond 3x
  and 5 ms, judged with at least 20 samples per side (runner noise is large;
  a poll interval replacing a wake-up is a multiple);
- ClosedClean metrics (a higher maximum or a new boolean value) and markers the
  base reached that the head does not reach.

Every difference must be claimed by an entry this PR adds to
[`intended-changes.json`](../../.github/safety/intended-changes.json); an entry
whose change is not observed fails as well, so a contract change that the docs
promise but the code does not make is caught. Only entries added by the change
count, as with the coverage ledger. An entry names the operation class, an
optional dimension pattern, the change, the old and new behavior, the doc
sentence that promises it and the reason:

```json
{"call": "Dispose", "dimension": "mode=shared", "change": "new-exception",
 "before": "none", "after": "System.IO.IOException",
 "doc": "docs/x.md#cleanup failures now propagate", "reason": "..."}
```

`change` is `new-exception`, `exception-removed`, `outcome-change`, `latency`,
`closed-clean` or `marker`. The `doc` fragment quotes the promising sentence or
names a heading whose section mentions the call; `check_intended_changes.py`
validates both in the Safety policy job.
[test: .github/scripts/test_check_intended_changes.py#test_invalid_entries_fail_with_their_reason]

When a PR changes behavior on purpose: write the doc sentence with its anchor,
add the manifest entry, run the differential run locally and put its markdown
report in the PR description:

```bash
python .github/scripts/differential_run.py --base "$(git merge-base HEAD origin/dev)" \
  --targets chaos,concurrent --seeds 2947,102947 --count 30
```

Both trees need the outcome-emitting fuzz harness. The workflow skips with a
visible warning while the base lacks it. For an older tree, check it out as a
worktree, apply the harness commits on top and pass `--base-tree` (or
`--head-tree`); `--base-runs`/`--head-runs` compare existing run directories.

### Mutation on the diff

For PRs labelled `critical`, `mutation.yml` runs Stryker.NET
(`LiteDB.Tests/stryker-config.json`; scope `LiteDB/Client`,
`LiteDB/Engine/Services`, `LiteDB/Engine/Engine`) since the merge-base with
`TestingEnabled=true` in the environment, and `mutation_gate.py` lists surviving
mutants on changed lines. A survivor in cleanup or lock code (a `Dispose`,
`Close`, `Release*` or `*Finally` method, a `finally` block, or a type or file
named for a lock, gate, monitor, mutex, pin, turnstile or lifetime) fails the
job; the rest are advisory. Survivors are lines to write behavior tests for,
not a score to raise. It is not run on every PR because even a narrow diff
costs tens of minutes. Run it locally from a regular clone; Stryker resolves a
linked `git worktree` to the main checkout and diffs the wrong tree:

```bash
cd LiteDB.Tests
TestingEnabled=true dotnet stryker --since:$(git merge-base HEAD origin/dev) --output /tmp/stryker
cd .. && python .github/scripts/mutation_gate.py /tmp/stryker/reports/mutation-report.json \
  --base $(git merge-base HEAD origin/dev)
```

## Regression proofs

A fix proves its regression test against a **real** state in which the bug
existed, not a synthetic mutant. Mutants test oracles; they do not show that the
test detects the bug that actually occurred. Pin the strongest state available:

1. a published NuGet package containing the bug (`package`);
2. a commit that existed on `dev` (`dev-commit`);
3. only when the defect was introduced and fixed before reaching `dev`, a commit
   of the originating PR (`pr-commit`). A PR of another repository (a fork) also
   names it: `"repository": "owner/name"`. Without that field the PR number
   means this repository's PR, so a fork commit fails provenance with a message
   that names the field.

Record the proof in
[`regression-proofs.json`](../../.github/safety/regression-proofs.json). It
names a [ReproRunner](../reprorunner.md) repro whose package variant pins the
known-bad state: the published version, or `0.0.0-knownbad.<first 12 of the
commit>` for commits. The repro is `green`, its package variant must reproduce and
its latest variant must not. The proof also names the permanent guard: the tests,
fuzz targets or scripts that keep the regression covered afterwards. The repro is
a black-box program, so the same scenario runs against both revisions even when
the fix adds hooks the old code lacks.

The **Regression proof** workflow runs this lifecycle:

- **Pull request.** For each proof the PR adds or changes, it verifies that the
  package exists on nuget.org or that the commit is on `dev` or in the PR (its
  `refs/pull/<pr>/head`, fetched fresh from `origin` or from the public
  `repository`; a failed fetch is an error, never a stale local ref). Commit
  states are packed into a local feed. Then the known-bad state **must fail** and
  the candidate **must pass**. The repro's own configuration output proves which
  LiteDB each run loaded.
- **After merge.** The push to `dev` repeats the proof on the integrated revision.
  That run is the retirement evidence.
- **Retired.** The historical comparison runs again only when its proof or repro
  changes, when the proving harness changes (the workflow, its scripts or the
  ReproRunner CLI and shared code; this re-proves every entry), or on manual
  dispatch. The permanent guard stays in the ordinary suites, and removing part
  of it is a coverage finding.

**A bug-fix PR must add at least one regression proof**: a new proof, or an
existing one re-pinned to a new known-bad state. A bug-fix PR is one labelled
`bug`, or `bugfix-fix` (the automated bugfix worker's fixes); `BUG_LABELS` in
`regression_proof.py` is the list. A PR fixing several bugs adds one proof per
bug. Without it, the Regression proof check fails. The check runs on every PR and
again when labels change, so the rule also applies when the label is added later.
Scaffold the repro and its entry in one step:

```bash
python .github/scripts/regression_proof.py new --id Issue_1234_ShortName --issue 1234 \
  --title "What goes wrong" --guard "LiteDB.Tests/Issues/Issue1234_Tests.cs#The_regression_test"
```

It pins the newest published package by default (`--known-bad` accepts
`package:<version>`, `dev-commit:<sha>`, `pr-commit:<sha>@<pr>` or, for a fork's
PR, `pr-commit:<sha>@<owner>/<name>#<pr>`), and its
`Program.cs` throws until the reproduction is written, so an unfinished repro
fails the proof. Do not add new repros to `LiteDB.sln`: a repro pinned to a
packed commit cannot restore in the ordinary build.

Every PR shows its counts in the checks list as an informational check named like
`Evidence: +12 tests · 2/2 proven to fail before` (tests added, minus tests
removed, and the regression proofs whose known-bad state failed while the PR head
passed). After the run, the **PR evidence labels** workflow sets
`regression: proven` or, on a bug-fix PR without a passing proof,
`regression: needs proof`. It runs trusted code from `dev` and treats the run's
`pr-evidence.json` as data, so it also labels fork PRs. The counts come from code
the PR controls, so both the check name and the labels are advisory. The labeler
confirms what it can itself: the PR's labels and changed files come from the API,
and the run's conclusion from the event. A PR that changes the proving harness
(the proof or labeler workflows, their scripts, or the ReproRunner CLI and
shared code) never gets `regression: proven`. The repro itself is written by the
PR, so a reviewer still checks that it reproduces the reported bug. The Evidence
check's name changes with the counts; never make it a required check.

To reproduce a commit state locally, run
`python .github/scripts/regression_proof.py pack-known-bad --commit <sha> --feed
<dir>` (add `--pr <n> [--repository owner/name]` to fetch a PR commit the clone
lacks), set `RestoreAdditionalProjectSources=<dir>`, then run the repro with
ReproRunner.

## Net proofs

A regression proof shows that a *fix* is covered by a black-box repro. A **net
proof** shows that a general *safety net* (an oracle, the wait-for graph, a fuzz
target, a lint) detects a defect it was not written for: the net fires with its
expected assertion at the known-bad commit and stays quiet at the fix. Entries
live in [`net-proofs.json`](../../.github/safety/net-proofs.json) and use the
same `knownBad` provenance (`dev-commit`, or `pr-commit` with `repository`;
packages cannot be overlaid). `level` says whether a net not written for the bug
fired (`generic`) or a bug-specific test was turned into an attributable yell
(`reproduction`); never present one as the other. `independence` records
`tuned-after-fix: <why>` when the net was changed after reading the fix.

`net_proof.py run --id <id>` checks out both commits as worktrees, applies the
net's overlay (cherry-picked commits, patches, and adapter directories from
[`tools/net-proofs/adapters/`](../../tools/net-proofs/README.md) that compile only
against the historical trees), builds, and runs the command under a hard
wall-clock limit. Its result is `proven` or one of `not-fired`, `fired-at-fix`,
`fired-differently`, `not-reproduced`, `harness-error`, `not-applicable`; only
`proven` passes.

**Capabilities.** The ledger's capability table declares a probe (a file, or a
regular expression over files) per capability, such as `handle-api`, which only
the fork's JKamsker/LiteDB#133 tree has. Each tree is probed before and after its overlay. When a
capability the net `requires` is missing, the proof is **not applicable** with
the missing names: it is reported and fails the run, and it never counts as
passing. `net_proof.py capabilities --rev <rev>` lists what a revision has.

**Evidence classes** set the re-run rule:

| Class | Nets | Rule |
| --- | --- | --- |
| 1 | Controlled schedules, abstract models | The firing must replay with the same assertion (`replays`, default 1), else `not-reproduced` |
| 2 | Native threads, multiple processes | `runs` (default 3) per side; one matching firing proves, the fire rate is recorded, and any firing at the fix is kept as a finding |
| 3 | Performance | `runs` (default 5) alternating between the sides; a side fires when its share of failing runs reaches `tolerance.fireFraction` (the command applies the measured tolerance); `tolerance.metric` records the values |

A non-reproduction is classified (schedule-dependent, environment-dependent, or
harness nondeterminism) before any conclusion. CI validates the ledger offline
in the Safety policy job. Proofs run only from the manual **Net proofs**
workflow, never on every PR.

## CI evidence

The final **Safety evidence** job of the build-and-test workflow fails unless the
run is complete, current and executed:

- every job declared for the tier in
  [`ci-evidence.json`](../../.github/safety/ci-evidence.json) succeeded, and no
  skipped, cancelled or undeclared job remains;
- every expected test leg uploaded evidence and none is unexpected;
- every leg tested the validated revision with binaries built from it;
- every partition has a non-empty, cleanly finished result and passed the runtime
  guard;
- every test the leg discovered produced a result, so a truncated session cannot
  look green;
- every test in `LiteDB.Tests` executed on at least one leg, or is quarantined.
  This includes tests compiled out of Release builds.

Changing a CI matrix therefore means updating `ci-evidence.json`, which is itself
a coverage change. The job uploads a `safety-evidence` summary with the revision,
legs and counts: link that run in the PR's Evidence prompt instead of pasting
logs. A re-run keeps the earlier attempt's failures, so classify them in the PR.
The job runs from the PR merge ref with a read-only token and never needs
privileged credentials for fork code.

## Candidate-tree validation

PR CI tests the merge of the head with the current base. A green result from an
earlier head or base is not evidence for a later integrated tree; relevant head,
base or stack changes invalidate it. `ci.yml` and the Safety section workflow
accept `merge_group`, so a merge queue for `dev` needs only the repository
setting. Enabling it means requiring **build-and-test / Safety evidence** and
**Safety section**. Queue runs carry no PR labels, so they use the PR tier; a
change that needs the full tier on its candidate tree must get it before it is
queued. Fuzz and index-migration workflows have no `merge_group` trigger and must
not be made queue-required checks as they are. Until a queue is
enabled, serialize high-risk merges: update the branch with `dev`, wait for a
fresh green Safety evidence run on that merge ref, then merge. The scheduled full
CI run is the post-merge backstop.

Repository settings are unchanged for now: no check is required and there is no
merge queue, so a check that fails to start cannot block every merge. The merge
gate is **build-and-test / Safety evidence**: it runs on every PR (and accepts
`merge_group`), and it requires every declared job, including the always-run
**Oracle smoke** job (oracle self-tests and the oracle-wired fuzz targets at a
small count), so the candidate tree carries oracle evidence even when the
path-filtered Fuzz workflow does not run. A maintainer enables it in the
repository settings: branch protection (or a ruleset) for `dev`, "Require status
checks to pass", add `build-and-test / Safety evidence`. Never require
path-filtered workflows (Regression proof, Fuzz, index migration): a required check
that does not run stays pending and blocks the PR.

## Findings, gates and audits

Classify each failure as an introduced regression, a pre-existing defect newly
exposed, a harness or CI defect, a documented semantic difference, or unknown
origin, with failing and fixed revisions where known. A new unexplained safety
failure pauses merges touching that protocol until it is classified. Release
dispositions belong in #2623. Before a high-risk merge, agree on a finite gate:
the affected contracts, the scenario and fault matrix, platforms, semantic
coverage assertions and run budget. The gate is met when that evidence passes
on the accepted candidate and all in-scope failures are classified. A new
concrete counterexample reopens it; unrelated improvements are follow-ups.

Integrated audits freeze a revision and start from the contracts and the
registries, not from a PR's narrative. Run one weekly during active storage and
concurrency work, after related protocol merges and before release
qualification. The repository maintainer owns them until a rotation is defined.
Reports go to `litedb-org/LiteDB-Artifacts` under
`audits/<date>-<short-sha>/`, recording the revision and baseline, the
contracts, scenarios and models checked, coverage and replay references,
classified findings with owners, blocked decisions and untested scope, and the
next investigation.
