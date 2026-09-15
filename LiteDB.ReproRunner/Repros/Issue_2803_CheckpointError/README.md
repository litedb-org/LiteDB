# Issue 2803: historical comparison

A one-time access-denied error on the checkpoint data stream follows the successful EnsureIndex commit. Independent reopened data and unique-index checks precede the assertion that the original error reaches the caller. This proves post-commit rollback masking, as described in the issue’s fork evidence; the original SynchronizationLockException trace is still unconfirmed.

```sh
dotnet run --project LiteDB.ReproRunner/LiteDB.ReproRunner.Cli -c Release -- run Issue_2803_CheckpointError --report /tmp/issue-2803.json --report-format json
```

The package control uses LiteDB 5.0.10; the other variant builds current source.
Each process runs 3 attempts by default. `LITEDB_REPRO_ATTEMPTS` changes that
count. Output records attempts and observed failures; all attempts must pass to
report no reproduction. Unrelated failures exit 20, so they cannot masquerade as
a fix. The process has a 180-second timeout; increase the runner timeout for
larger repeat counts. An intermittent all-pass run is evidence, not proof of a fix.
