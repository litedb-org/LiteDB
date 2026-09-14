# Issue 2169: historical comparison

Eight parallel inserts encounter one checkpoint access-denied error after commit. Every committed identity and payload, including the insert whose caller received an error, must survive. Only the reported disposed-rollback error counts as reproduction; this identifies a concrete storage-failure trigger, without claiming it was the reporter’s unknown original trigger.

```sh
dotnet run --project LiteDB.ReproRunner/LiteDB.ReproRunner.Cli -c Release -- run Issue_2169_CheckpointError --report /tmp/issue-2169.json --report-format json
```

The package control uses LiteDB 5.0.11; the other variant builds current source.
Each process runs 3 attempts by default. `LITEDB_REPRO_ATTEMPTS` changes that
count. Output records attempts and observed failures; all attempts must pass to
report no reproduction. Unrelated failures exit 20, so they cannot masquerade as
a fix. The process has a 180-second timeout; increase the runner timeout for
larger repeat counts. An intermittent all-pass run is evidence, not proof of a fix.
