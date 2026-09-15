# Issue 2812: cross-runtime collation changes corrupt index semantics

This repro covers [LiteDB #2812](https://github.com/litedb-org/LiteDB/issues/2812). A datafile's
string index is ordered by the runtime globalization implementation that created it, but LiteDB
persists only the culture LCID and compare options. Reopening the file under another implementation
can make an index seek silently pass existing keys.

The parent process creates two `de-DE/None` databases in child processes: one with the host
globalization implementation and one with invariant globalization. It reads each database first in
the creating environment as a positive control and then in the other environment. A BCL sort-order
fingerprint proves that the two environments actually disagree before the cross-environment result
is accepted.

Each database contains the same immutable ledger twice: string primary keys and a unique secondary
string index. Full traversal, count, exact key/ordinal/payload tuples, and a separately persisted
sort fingerprint establish that the data still exists. `FindById` and `Query.EQ` then probe both
index paths. Finally, isolated copies upsert every string `_id` and must persist exactly 300 updated
rows, so a scan fallback added only to public reads cannot conceal broken unique-key writes. Every
read is read-only and the datafile bytes must remain unchanged.

Exit 0 plus `BUG_2812_CONFIRMED` requires a cross-environment indexed miss, duplicate `_id`, or wrong
persisted update while the controls prove the fixture and same-environment behavior. Exit 10 plus
`VERIFIED_2812` requires both read paths and unique-key upserts in both directions to preserve the
exact ledger, or an explicit `LiteException` that identifies collation/globalization/sort as the cause
and tells the caller to rebuild. Exit 20 is a failed control or unrelated harness error and must never
count as a fix.

Run both the released package and the in-repository source:

```sh
dotnet run --project LiteDB.ReproRunner/LiteDB.ReproRunner.Cli -- run Issue_2812_CollationProcess
```

When the bug is fixed, change the latest expectation to `kind: noRepro`, `exitCode: 10`,
`logContains: VERIFIED_2812` and set the state to green. Keeping the exit code and marker constraints
prevents a crash, timeout, skipped precondition, or vague rejection from making the regression green.

## Last verified

Verified against dev source `094f2b8564d65ae37951e4d15104c624c53dafdd` from PR #2877 at working
tree `89820a3b0a8575e6d126d0b7ff6a9b920dc2078b`, on Ubuntu 24.04 x64 with .NET SDK
10.0.400 and .NET 8.0.30 runtime.

Commands executed:

```sh
dotnet run --project LiteDB.ReproRunner/Repros/Issue_2812_CollationProcess/Issue_2812_CollationProcess.csproj -c Release -p:UseProjectReference=true --no-restore
dotnet run --project LiteDB.ReproRunner/LiteDB.ReproRunner.Cli -- validate --id Issue_2812_CollationProcess
dotnet run --project LiteDB.ReproRunner/LiteDB.ReproRunner.Cli -- run Issue_2812_CollationProcess --report /tmp/issue-2812-report.json
```

The source run reproduced the bug in both directions. Host-to-invariant lost 264/300 primary and
231/300 secondary lookups, then upsert persisted 564 rows instead of 300. Invariant-to-host lost
235/300 primary and 261/300 secondary lookups, then upsert persisted 535 rows. Same-environment reads
had zero misses and both same-environment upsert controls persisted exactly 300 updated rows. Manifest
validation passed; the runner's package 5.0.21 and source variants both exited 0 with the required bug
marker.

This deterministic probe covers host ICU versus invariant globalization. It does not independently
rerun Windows NLS/ICU or historical OS sort-table pairs, the reporter-provided databases, FileStorage,
or `StartsWith`; those are alternate manifestations of the same persisted-index ordering defect.
