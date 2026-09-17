# Issue 2775: growing scan-address retention

Source: https://github.com/litedb-org/LiteDB/issues/2775

The process-isolated ReproRunner test checks all 360,000 identities and payloads,
then measures retained heap while the scalar-key iterator is still alive. It
allows 4 MiB of growth after the first third. Disposal cannot hide the retained
state. Dev receives an 8 MiB cache and a 100-page transaction bound; old packages
without these settings explicitly report the difference.

Observed on dev `90788bace880f477c9fb52ff7c0da98c9700bdfe` and 5.0.21:

```text
Package:
SETTINGS boundedCache=False, boundedTransaction=False
MEASUREMENT rows=360000, additionalRetainedBytes=35730704
BUG_2775_CONFIRMED: a scalar-key scan retained more than 4 MiB of additional state after its first third
Latest:
SETTINGS boundedCache=True, boundedTransaction=True
MEASUREMENT rows=360000, additionalRetainedBytes=10324472
BUG_2775_CONFIRMED: a scalar-key scan retained more than 4 MiB of additional state after its first third
```

Run `dotnet run --project LiteDB.ReproRunner/LiteDB.ReproRunner.Cli -c Release -- run Issue_2775_ScanRetention`.
The package retains its known-failure expectation (exit 0 + BUG_2775_CONFIRMED).
The fixed source must produce exit 10 + VERIFIED_2775 and retain all row/checksum checks.

Scope: this catches the common index-address retention; it does not separately
bound the aggregate pipeline's additional address cache. Increase
`LITEDB_REPRO_ROWS` to investigate larger workloads in a fresh process.

The manual fix measures -3,328 bytes of additional retained heap at the unchanged
360,000-row scale, versus 10,324,472 bytes on the preceding source. Four separate
million-row aggregate processes also verify exact COUNT, SQL COUNT, filtered COUNT
and SUM results: peak additional managed heap falls from about 44–47 MB to
31–46 KB. These are Linux x64/net8.0 production observations with an 8 MiB cache
and 100-page transaction budget, sampled after warming the first 100,000 rows.
Multiple-aggregate and grouped replay remain outside this streaming optimization.
