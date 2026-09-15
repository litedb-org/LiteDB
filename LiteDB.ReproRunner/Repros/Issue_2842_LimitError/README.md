# Issue 2842: historical comparison

This process runs the actual `Issue2842_Tests` regression three times against
LiteDB 5.0.20 and current source. A fixed result requires all three complete
runs, including their independent persistence/recovery assertions, to pass.
Only the reported failure is accepted as a bug outcome; unrelated failures are
exit 20. The per-attempt output and `REPEATS_2842` line retain the observed
failure rate. ReproRunner bounds the entire process to 120 seconds.

```sh
dotnet run --project LiteDB.ReproRunner/LiteDB.ReproRunner.Cli -c Release -- \
  run Issue_2842_LimitError --report issue2842.json --report-format json
```

A green comparison means the historical bug/current-source expectations matched;
the actual counts and outcomes are in the report, not just the CI job status.
