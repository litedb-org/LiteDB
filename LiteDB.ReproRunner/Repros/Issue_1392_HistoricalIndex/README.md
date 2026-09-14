# Issue 1392: historical index workload

```sh
dotnet run --project LiteDB.ReproRunner/LiteDB.ReproRunner.Cli -c Release -- \
  run Issue_1392_HistoricalIndex --report /tmp/issue1392.json --report-format json
```

The retained regression executes three times on 5.0.9 and current source. Both
variants check every indexed read, upsert result, final identity, and payload
after reopening. Current source additionally must demonstrate actual eviction
under a 64 KiB cache, zero lost frames, and zero lingering pinned pages. Historical
5.x predates that configurable cache, so its run uses the original cache policy
and explicitly records the difference. Removing the current cache API cannot
silently disable its assertions.

All three attempts passed on 5.0.9 and dev `a50661a9` on Linux/.NET 8.
An additional run against the originally reported `5.0.0-beta` compiled and ran,
but failed a missing-row assertion during an indexed lookup, without the reported
`NotImplementedException`/`ReadIndexKey` trace. It exited 20 as an unrelated
failure and is not counted as either a faithful reproduction or a passing beta
comparison. Run that exploratory variant with:

```sh
dotnet run --project LiteDB.ReproRunner/Repros/Issue_1392_HistoricalIndex -c Release \
  -p:LiteDBPackageVersion=5.0.0-beta
```

The original damaged index file and key/document are unavailable. No invalid
key byte is manufactured to make the test fail. A no-reproduction outcome applies
to these attempted workloads, not to every possible beta-era workload.
