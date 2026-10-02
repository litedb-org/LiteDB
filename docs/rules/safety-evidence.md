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
replace the prompts with one `- Not applicable: <reason>` line. A critical change
(the `critical` label, set from the diff by `classify_critical.py`) also adds the
generated `Critical change evidence` section; see
[implement safely](implement-safely.md#the-pr-description).

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
gaps. A contract's `claims` map normative documentation sentences to the
evidence that establishes them, or to an explicit gap (the
[semantic review](#semantic-review) record). Every reference must resolve. Fuzz targets must run in `fuzz.yml`, and
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
reason, a linked `issue`, a `review` date, an `expires` date and the gap it leaves;
an entry without them fails. A quarantined test is a visible coverage gap, never
passing evidence. A passed review or expiry date is reported on every run. The
weekly **Quarantine re-run** workflow (`quarantine_rerun.py`) runs every quarantined
test, with its `Skip` removed in that run's checkout only, and every known-finding
test (a method named `Known_finding_*` that pins a defect's current behavior). It
fails when a quarantined test passes, a known-finding test fails, an entry produces
no result, or a date has passed.

## Diff nets

These nets judge the change against its base. Each one answers a question a diff
reviewer cannot answer reliably. None of them proves the code correct.

| Net | Catches | Runs |
| --- | --- | --- |
| [Diff lints](#diff-lints) | new polling loops, deleted invariant comments, unanchored doc claims | every PR (Safety policy, Safety section) |
| [Differential run](#differential-run) | normalized behavior changes nobody declared, and declared changes that did not happen | PRs touching `LiteDB/` |
| [Mutation on the diff](#mutation-on-the-diff) | changed cleanup and lock lines that no test pins down | critical PRs, manual |
| [Contended acquire](performance.md#contended-acquire) | acquire-latency tails, overtaking and waiter progress | the writer-contention step of the shared-slot performance workflow |

**Pilot: nothing blocks yet.** [`net-modes.json`](../../.github/safety/net-modes.json)
is the single switch for all of them. While its `blocking` is `false`, each net
reports its findings in the job summary and annotations and exits 0. One
reviewed change to `true` makes every listed net fail its job on findings, once
the pilot numbers are reviewed. The scripts take `--advisory` or `--blocking` to
override the switch locally. Jobs do not use `continue-on-error`, which would
also hide a crashing script.

### Diff lints

The Safety policy job runs three lints on the lines a PR adds or removes:

- `lint_polling.py`: a timed wait (`WaitOne(n)`, `Monitor.Wait(x, n)`,
  `SemaphoreSlim`/`ManualResetEventSlim.Wait(n)`, `WaitAny(.., n)`), a sleep,
  `Task.Delay` or a spin added inside a loop in `LiteDB/` needs
  `// polling: <reason>` on its line or within the two lines above. A waiter that
  times out and retries loses its place in the queue, so later arrivals can win
  the handoff and tail latency grows with the interval.
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
  `propagate`, `never`, `always`, `rejects`, `refuses` or `guaranteed` needs
  either an inline anchor that resolves, `[test: <path-or-Class>#<Method>]` or
  `[marker: <name>]`, or a claim in
  [`contracts.json`](../../.github/safety/contracts.json) (see
  [Contracts and fault points](#contracts-and-fault-points)). Unchanged
  sentences are not judged; `--all` lists the backlog.

An anchor proves that a reference exists, not that the test establishes the
sentence. That judgment is the [semantic review](#semantic-review).

### Semantic review

For a [critical](validation.md#scale-evidence-with-complexity-and-persistence-risk)
change, a reviewer who did not write the change reads every normative sentence
the change adds or edits, together with the evidence named for it, and records
the result as a `contracts.json` claim:

```json
{"doc": "docs/x.md", "sentence": "cleanup failures now propagate from Dispose",
 "evidence": [{"test": "LiteDB.Tests/X_Tests.cs#Method", "proves": "the observed event that establishes it"}]}
```

`proves` states what the test observes, and must cover every mode and caller
the sentence covers. A sentence whose evidence covers only part of it is
narrowed, or recorded with `"gap": "<what is not covered>"`. `check_contracts.py`
fails when a claimed sentence is no longer in its document, so an edited
promise is reviewed again. A behavioral promise also gets an
[`intended-changes.json`](#differential-run) entry, so the differential run
checks that the code actually changed.

### Differential run

`differential_run.py` builds the merge-base and the head in separate worktrees
(`TestingEnabled=true`) and runs the same fuzz targets, seeds and counts on each
tree's own harness. Seeds are fixed per PR number. It compares **normalized
outcomes** per operation class and dimension (`op`, `dimension` in
`outcomes.jsonl`):

- the set of outcome kinds observed. An outcome outside a record's declared
  `permitted` set (`ok`, `threw`, `threw:Type#code`, ...) fails on its own. A
  racing operation declares the outcomes it may legally end in; when both trees
  declare the same set, which of them a run hit is a permitted variation, not a
  difference;
- the exception contract: the types (and error codes) that escape, and whether
  the primary failure survives when cleanup also fails (`primaryExceptionType`);
- payloads and acknowledged effects (`payloadDigest`, `effectsDigest`), compared
  as multisets;
- cleanup obligations: a `ConnectionClean` or `Quiescent` evaluation that is
  unclean, or shows a violation kind, where the base was clean; a `FaultDisposed`
  declared-to-observed disposition the base did not show at the same teardown
  path, fault site and injector model (fail-inside or skip). A site only the
  head exercised is compared with every disposition the base showed on that
  path. The difference's dimension is `site=<site>;model=<model>`;
- markers and fault points the base reached that the head no longer reaches.

Timings, raw traces, metric values and the order of legal concurrent winners
are not compared. Performance is separate evidence. Concurrent targets are
native-thread evidence: with `--repeat N` (the workflow uses 2), an operation
whose outcome set varies between runs of one tree is reported as
schedule-dependent instead of failing. Repeats cannot classify a rare race that
one tree happens not to hit, so declaring `permitted` is the real remedy. A field the harness does
not record on both sides is listed as *not compared*. Operation classes and
markers seen only on the head are listed as capabilities, not diffed.

Each key carries its exercise count on both trees, and the report states
whether it was exercised and changed, exercised and unchanged, or not
exercised on the base or on the head. "Unchanged" is evidence. "Not exercised"
is none.

A fuzz run stops at its first failure, so a head run that failed at step N did
not run the later steps of the same target, seed and repeat. An operation class
missing on the head is grouped under that run instead of reported as "no longer
exercised" only when this holds for every base run that shows the class:

- the head run of the same target, seed and repeat stopped early at step N;
- the base run first shows the class at step N or later.

A class the base showed at a step the head run reached is still a finding.
The same holds for a class whose head run completed or is missing, and for a
record without a step. Grouped classes are *not compared*. The report gives one
note per head run: target, seed, repeat, stop step, failure id, number of
classes and examples. The note fails the run once (the manifest cannot cover
it) when the base run of that schedule completed every step: the head
introduced the failure that cut the comparison short. When both runs stopped
early, the note does not fail. The fuzz failures are the fuzz targets'
findings. Lost markers and fault points are never grouped.

Every remaining difference must be claimed by an entry this PR adds to
[`intended-changes.json`](../../.github/safety/intended-changes.json). An entry
whose change is not observed fails as well, so a promised contract change that
the code does not make is caught. The report says which case it is:

- *exercised, unchanged*: both trees ran the claimed call, and the claimed
  change did not happen;
- *not exercised* on the base, the head or either side: the run has no evidence
  about the claim. Add a target or seed that reaches the call. Only entries added by the change count, as
with the coverage ledger. An entry names the operation class, an optional
dimension pattern, the change, the old and new behavior, the doc sentence that
promises it and the reason:

```json
{"call": "Dispose", "dimension": "mode=shared", "change": "new-exception",
 "before": "none", "after": "System.IO.IOException",
 "doc": "docs/x.md#cleanup failures now propagate", "reason": "..."}
```

`change` is one of `new-exception`, `exception-removed`, `primary-changed`,
`outcome-change`, `payload-change`, `effect-change`, `cleanup-change` or `marker`.
For a `cleanup-change` of a `FaultDisposed` row, `call` is the teardown path.
`"dimension": "site=LiteEngine.Close.checkpoint;*"` limits the entry to one
fault site.
The `doc` fragment quotes the promising sentence, or names a heading whose
section mentions the call; `check_intended_changes.py` validates both in the
Safety policy job.

When a PR changes behavior on purpose:

1. Write the doc sentence and its contracts.json claim.
2. Add the manifest entry.
3. Run the differential run locally and put its markdown report in the PR
   description:

```bash
python .github/scripts/differential_run.py --base "$(git merge-base HEAD origin/dev)" \
  --targets chaos,concurrent --seeds 2947,102947 --count 30
```

Both trees need the outcome-emitting fuzz harness. The workflow skips with a
visible warning while the base lacks it. For an older tree, check it out as a
worktree, apply the harness commits on top and pass `--base-tree` (or
`--head-tree`). `--base-runs`/`--head-runs` compare existing run directories.

### Mutation on the diff

Mutation on the diff is a pilot. Critical PRs (labelled, or classified from the
diff), and manual runs, execute `mutation.yml`, which calls `.github/scripts/run_mutation.py`. The helper:

- runs the pinned `dotnet-stryker` (`.config/dotnet-tools.json`) with the
  committed `LiteDB.Tests/stryker-config.json` (passed with `--config-file`),
  `--since` the full merge-base SHA;
- sets `TestingEnabled=true` and `TargetFramework=net8.0` in the environment,
  because the tests call TESTING hooks and the projects are multi-target;
- mutates changed files in `LiteDB/Client`, `LiteDB/Engine/Services` and
  `LiteDB/Engine/Engine`;
- from a linked `git worktree`, runs in a temporary clone of the worktree's
  HEAD. Stryker resolves a worktree to the main checkout, and uncommitted
  changes are not mutated.

`mutation_gate.py` lists the surviving mutants (Survived, NoCoverage) whose span
touches a changed line. They are lines to write behavior tests for, not a score;
no score threshold applies. A survivor in cleanup or lock code is classed
*blocking*, and the class fails the job only once the switch is on. Cleanup or
lock code means:

- the body of `Dispose`, `DisposeAsync`, `Close`, `CloseAsync`, `Release*` or
  `*Finally`, or a finalizer;
- a `finally` block;
- a type or file whose name contains the word lock, gate, monitor, mutex, pin,
  turnstile or lifetime.

Lines that a Release build without `TestingEnabled` cannot compile are not
product code, so they never block
[test: .github/scripts/test_mutation_gate.py#test_survivor_compiled_only_under_debug_or_testing_is_advisory]. Such a line is inside an `#if`, `#elif` or
`#else` branch that is false whenever `DEBUG` and `TESTING` are undefined:
`#if DEBUG || TESTING`, `#if TESTING`, `#if DEBUG`, or anything nested in
them. Every other symbol counts as unknown, so the `#else` of
`#if DEBUG || TESTING` stays product code. Survivors on such lines are
listed in their own advisory bucket ("DEBUG/TESTING only"), with their count.

Survivors elsewhere are advisory. Timeout counts as detected. CompileError,
RuntimeError and Pending mutants in that code are reported as not evaluated:
Stryker discards every mutant of a method when one does not compile. A report
that does not match the head revision fails in either mode: unreadable, a file
with mutants whose embedded source differs from the head, or a report key that
matches several changed files. A file without mutants is skipped and counted,
because there is no line to map. Stryker 5 lists every file outside the mutate
scope with the placeholder source `File ignored by mutate filter`. Survivors
never fail an advisory run
[test: .github/scripts/test_mutation_gate.py#test_advisory_mode_exits_zero_with_findings_and_placeholders].

Changed C# test files broaden the mutant set. Stryker treats a changed test as
invalidating every mutant its tests cover, so a test-only change re-tests all
of them; the survivor list still covers changed lines only. A changed file
under `LiteDB.Tests/` that does not end in `.cs` (a fixture, the `.csproj`,
the Stryker config) would make `--since` re-test the whole mutate scope.
The helper adds each such file to `since.ignore-changes-in` in the config it
passes, so only C# changes decide which mutants run. Every mutant of a
changed source file runs either way. The run is not per
PR because every mutant of a changed file is tested, and the cost is not yet
measured on the full suite. Survivor outcomes are measured per run, not predicted.

```bash
python .github/scripts/run_mutation.py --base "$(git merge-base HEAD origin/dev)" --output /tmp/mutation \
  [--test-case-filter 'FullyQualifiedName~X']
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
Two more levels keep weaker evidence apart: `model` (an abstract model of the code
fired, not the library) and `tuned-after-fix` (the net, scenario or rule was changed
after reading the fix or its subject; such an entry says what in `independence`). A
`generic` or `model` entry is designed from the invariant; `validate` checks both.

`net_proof.py run --id <id>` checks out both commits as worktrees, applies the
net's overlay (cherry-picked commits, patches, and adapter directories from
[`tools/net-proofs/adapters/`](../../tools/net-proofs/README.md) that compile only
against the historical trees), builds, and runs the command under a hard
wall-clock limit. Its result is `proven` or one of `not-fired`, `fired-at-fix`,
`fired-differently`, `not-reproduced`, `harness-error`, `not-applicable`; only
`proven` passes.

**Recorded evidence.** A proof run outside `net_proof.py` (for example on a replay of a
fork's history with the nets on every commit) is an entry with `net.recorded` and no
`command`: the tree it ran on (`overlay`, a key of the ledger's `recordedOverlays`; the
replay commits as `knownBadTree`/`fixTree`), the command that ran, and `results` with
`recorded: true`, its `source` record and `verifier`. `results.knownBad.fired` and
`results.fix.fired` say whether the net yelled for *this* defect; unattributed yells go
to `otherFindings`. `run` reports such an entry as `not-attempted` without a checkout,
so CI does not re-run it and does not count it. The optional `defects` table numbers the
ledger rows (known-bad, fix, kind; `alsoKnownBad` for a defect-introducing commit that a
diff net judges), and `validate` checks every entry's commits against its row.
`render_net_proofs.py` renders the ledger into the
[proven obligations](implement-safely.md#proven-obligations) and the tables of the
[retrospective](../safety-net-retrospective.md); CI checks both are current.

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
