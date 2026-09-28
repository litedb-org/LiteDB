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
**Safety section**. Fuzz and index-migration workflows have no `merge_group`
trigger and must not be made queue-required checks as they are. Until a queue is
enabled, serialize high-risk merges: update the branch with `dev`, wait for a
fresh green Safety evidence run on that merge ref, then merge. The scheduled full
CI run is the post-merge backstop.

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
