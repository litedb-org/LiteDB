# Issue 2561 – TransactionMonitor finalizer crash

This repro demonstrates [LiteDB #2561](https://github.com/litedb-org/LiteDB/issues/2561). The
`TransactionMonitor` finalizer (`TransactionService.Dispose(false)`) executes on the GC finalizer thread,
which violates the monitor’s expectation that the transaction belongs to the current thread. The
resulting `LiteException` brings the process down with the message `current thread must contains
transaction parameter`.

## Scenario

1. Open a `LiteDatabase` connection against a clean data file.
2. Begin an explicit transaction on the main thread and perform a write.
3. Use reflection to grab the `TransactionMonitor` and the `TransactionService` tracked for the main thread.
4. Invoke `TransactionMonitor.ReleaseTransaction` from a different thread to mimic the finalizer’s behavior.

The monitor throws the same `LiteException` that was captured in the original crash, proving that the
guard fails when the finalizer thread releases the transaction.

## Running the repro

```bash
dotnet run --project LiteDB.ReproRunner/Repros/Issue_2561_TransactionMonitor/Issue_2561_TransactionMonitor.csproj -c Release -p:UseProjectReference=true
```

By default the project references the NuGet package (`LiteDBPackageVersion` defaults to `5.0.20`). Pass
`-p:LiteDBPackageVersion=<version>` to pin a different package, or `-p:UseProjectReference=true` to link
against the in-repo sources. The ReproRunner CLI orchestrates those switches automatically.

## Expected outcome

The repro prints the captured `LiteException` and exits with code `0` once the message containing
`current thread must contains transaction parameter` is observed.
