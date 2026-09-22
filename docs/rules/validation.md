# Validation and reproductions

Use this when adding regressions, diagnosing CI, or changing fuzz infrastructure.
Build commands and hook settings are in [development](development.md).
Storage and migration changes must satisfy the fault-injection, repeated-recovery,
and corruption-reporting checks in [data safety](data-safety.md).

## Scale evidence with complexity and persistence risk

The [database-safety completion gate](data-safety.md#database-safety-is-a-completion-gate)
applies to every implementation task. Select tests from the actual affected paths;
the following levels accumulate as the change crosses more boundaries.

| Change surface | Required evidence |
| --- | --- |
| Local behavior | Focused behavior/regression tests, boundary and error cases, and checks of affected callers |
| Shared query, mapper, serializer, or index behavior | Cross-path/differential tests, legacy representation coverage, and relevant broader regression suites |
| Stateful or concurrent behavior | Forced interleavings, ownership/rollback checks, repeated execution, and independent model/fuzz coverage for complex interactions |
| Database files, WAL, backups, temporary files, or streams | File-backed tests, partial/failed I/O and flushes, restart/recovery, preserved committed data and unrelated files, and corruption diagnostics at affected boundaries |
| Upgrade, checkpoint, replacement, or recovery protocols | Interruption at every changed persistent transition, repeated interrupted recovery, retry/idempotence, and old/new-state integrity checks |

Add cases for newly introduced states and their combinations. Verify the tests
reach the changed path and would detect the unsafe behavior they are meant to
exclude. A test that merely mirrors the implementation is insufficient evidence.

## Tests that distinguish a fix from a workaround

- Reproduce the reported API and conditions, with a failing-before/fixed-after
  comparison. A nearby symptom is not proof of the original issue. Where the
  current branch no longer reproduces, try the reported historical version and
  record exactly which versions and cases were checked.
- Assert observable behavior with an independent expected result. Include a
  positive control and boundary cases that defeat a trivial workaround such as
  rejecting all input, returning an empty result, or skipping a code path.
- Validate the claimed execution path. Creating an index does not prove the
  planner uses it; a small sort does not prove spill/merge behavior; a timeout
  does not prove the child reached the contested operation. Use plans, counters,
  barriers, and durable child markers as appropriate.
- For races, force the relevant interleaving and test lifetime/failure paths.
  Do not remove concurrency to make a test green. If the test itself races on
  unsupported shared state, isolate that state and retain concurrent engine calls.
- Run focused tests first, then the appropriate broader checks. xUnit and
  FluentAssertions tests live under the matching feature area. Keep filters
  broad enough to include newly split test classes; check the discovered count.
  Honor the 300-second session timeout in `tests.runsettings`.
- When a ReproRunner defect is fixed, update its source/package expectations,
  manifest state, and documentation together. A green known-bug reproduction
  is different from a passing regression guard. Configuration/handshake errors
  must fail even if the child prints a success marker or expected exit code.

## Runtime and environment coverage

Validate culture and timezone-sensitive changes outside UTC and invariant culture.
Include DST transitions, extrema, and indexed/unindexed comparisons where relevant.
For persisted collation changes, cover Windows/NLS and Linux/ICU.

Prove the runtime actually used: an x86 job label does not ensure an x86 test
host, and running a Framework-targeted DLL under .NET 8 does not test CLR 4.
Include child executables and support files in CI artifacts, then exercise the
packaged layout. See [ReproRunner](../reprorunner.md) and
[code generation](code-generation.md) for package/AOT validation.

## Fuzzing

Use [LiteDB.Fuzz](../../LiteDB.Fuzz/README.md) for commands, target definitions,
replay, retention, and campaign options.

- Start with bounded smoke/focused runs; long campaigns should match the task's
  requested budget. Independent targets may run in parallel. Concurrency targets
  must still exercise real overlapping operations or processes.
- Compare against an independent model or oracle. Cached versus uncached paths
  sharing one translator detect cache errors but can miss translator bugs;
  add CLR evaluation where semantics align and classify expected differences.
- Test the oracle with deliberately broken states. Record actual sort spills,
  cache eviction, crash points, and acknowledged operations, rather than inferring
  coverage from elapsed time or case counts.
- Preserve seeds, recorded input, trace hashes, exact binaries, runtime/config,
  and replay instructions. Save failure evidence before minimization and require
  the same failure identity when reducing. Preserve pinned regression hashes.
- Triage failures into product, harness, and expected semantic differences.
  Add confirmed bugs to the permanent corpus and focused regressions. Keep
  infrastructure changes and independent product fixes separately reviewable.
- Summarize findings in the PR when requested; large raw results belong in
  `litedb-org/LiteDB-Artifacts`, with links from the report. Keep local success
  artifacts bounded and preserve exact failure artifacts.

Relevant target selection matters more than rerunning everything. For reader
leases use `transaction-gate,cursor-handoff,concurrent`; for key-moving updates
use `index`; for replacement recovery use `rebuild-transition`; for flush/order
changes include `power-loss,recovery,wal` and the file-compatibility scripts.
