# Issue 2586 – Rollback fails after safepoint flush

This repro demonstrates [LiteDB issue #2586](https://github.com/litedb-org/LiteDB/issues/2586)
where a write transaction rollback can throw `LiteDB.LiteException` with the message
`"discarded page must be writable"` when many guard transactions exhaust the shared transaction
memory pool.

## Expected outcome

Running the repro against LiteDB 5.0.20 should print the captured `LiteException` and exit with
code `0`. A fixed build allows the rollback to complete without throwing, causing the repro to
exit with a non-zero code.

## Running the repro

```bash
# Run against the published LiteDB 5.0.20 package (default)
dotnet run --project LiteDB.ReproRunner/LiteDB.ReproRunner.Cli -- run Issue_2586_RollbackTransaction

# Run against the in-repo LiteDB sources
dotnet run --project LiteDB.ReproRunner/LiteDB.ReproRunner.Cli -- run Issue_2586_RollbackTransaction --useProjectRef
```

The repro integrates the `LiteDB.ReproRunner.Shared` library for its execution context and
message pipeline. `ReproContext` resolves the CLI-provided environment variables, and
`ReproHostClient` streams JSON-formatted progress and results back to the host.

## CI coverage

This fixed repro is disabled in automatic CI by `.github/repro-ci.json`.
The normal test suite covers the regression, including file-backed safety checks
in `Issue2586_RollbackSafety_Tests`. The historical package/source comparison remains
available with the local command above or manual Repro Runner dispatch with
`include-regressions: true`. See [the coverage policy](../../../docs/reprorunner.md#retiring-fixed-repros).
