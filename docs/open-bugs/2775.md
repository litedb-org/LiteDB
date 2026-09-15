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
The manifest expects the known failure (exit 0 + BUG_2775_CONFIRMED). A green
runner summary means reproduction matched, not that the bug was fixed. A repair
must produce exit 10 + VERIFIED_2775 and retain all row/checksum checks.

Scope: this catches the common index-address retention; it does not separately
bound the aggregate pipeline's additional address cache. Increase
`LITEDB_REPRO_ROWS` to investigate larger workloads in a fresh process.
