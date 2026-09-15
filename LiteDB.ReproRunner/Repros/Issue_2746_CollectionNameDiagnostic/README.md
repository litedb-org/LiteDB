# Issue 2746: collection-name diagnostic

```sh
dotnet run --project LiteDB.ReproRunner/LiteDB.ReproRunner.Cli -c Release -- run Issue_2746_CollectionNameDiagnostic --report /tmp/issue2746.json --report-format json
```

Runs the same diagnostic, recovery, and persisted-data assertions on 5.0.21 and
current source, three attempts each. Both reproduced on 2026-09-15. Exit 0 plus
`BUG_2746_CONFIRMED` means the exact unclear diagnostic was observed after the
other assertions passed; exit 10 means every assertion passed. Other exceptions
exit 20. `LITEDB_REPRO_ATTEMPTS` changes the count (1–1000).
