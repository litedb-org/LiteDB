# Issue 2825: historical comparison

This process runs the actual `Issue2825_Tests` regression three times against
LiteDB 5.0.20 and current source. A fixed result requires all three complete
runs, including their independent persistence/recovery assertions, to pass.
Only the reported failure is accepted as a bug outcome; unrelated failures are
exit 20. The per-attempt output and `REPEATS_2825` line retain the observed
failure rate. ReproRunner bounds the entire process to 120 seconds.

```sh
dotnet run --project LiteDB.ReproRunner/LiteDB.ReproRunner.Cli -c Release -- \
  run Issue_2825_FreeListRace --report issue2825.json --report-format json
```

A green comparison means the historical bug/current-source expectations matched;
the actual counts and outcomes are in the report, not just the CI job status.

The reported exception must be a `LiteException` with error code 999 and the exact
empty-page message from `Snapshot.NewPage`, `EngineState.Validate`, or the public
`LiteEngine.Insert` boundary. Concurrent historical rethrows can overwrite the
shared exception's original stack frames. Other
workers may concurrently fail in rollback (`discarded page must be writable`) or
index writes (`page must be writable to support changes`), or sending rollback or commit
pages to the already-closed WAL (`Cannot access a closed file.`). The commit case requires
`DiskWriterQueue.EnqueuePage`, `TransactionService.PersistDirtyPages`, and
`TransactionService.Commit` in the trace. The classifier permits
the disposed-transaction `CreateSnapshot` error only through the observed
`LiteEngine.Delete` / `AutoTransaction` path: engine shutdown disposes monitored
transactions while another worker can still be entering its collection.
It permits only those observed engine paths alongside the primary error, preserves their full
traces, and rejects them as standalone proof. Assertion failures and unknown errors
always fail the harness. Classifier tests cover these false-positive boundaries.

The process emits exception traces. ReproRunner's structured report retains the
last 200 output lines per variant, so a long trace can truncate earlier attempts;
the final repeat counter records the complete attempt count.
