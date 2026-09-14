# Issue 2814: historical comparison

The retained concurrent-reader test verifies every acknowledged payload after reopening before checking WAL growth.

```sh
dotnet run --project LiteDB.ReproRunner/LiteDB.ReproRunner.Cli -c Release -- run Issue_2814_WalGrowth --report /tmp/issue-2814.json --report-format json
```

The package control uses LiteDB 5.0.11; the other variant builds current source.
Each process runs 10 attempts by default. `LITEDB_REPRO_ATTEMPTS` changes that
count. Output records attempts and observed failures; all attempts must pass to
report no reproduction. Unrelated failures exit 20, so they cannot masquerade as
a fix. The process has a 180-second timeout; increase the runner timeout for
larger repeat counts. An intermittent all-pass run is evidence, not proof of a fix.
