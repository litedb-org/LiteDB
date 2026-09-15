# Issue 2816: historical comparison

One log metadata I/O failure during Dispose must still release the actual OS data-file handle. A successful run also reopens the database, checks the original payload, writes again, and verifies both writes. This tests cleanup after an external storage fault; it does not simulate Windows antivirus sharing semantics.

```sh
dotnet run --project LiteDB.ReproRunner/LiteDB.ReproRunner.Cli -c Release -- run Issue_2816_DisposeFailure --report /tmp/issue-2816.json --report-format json
```

The package control uses LiteDB 5.0.20; the other variant builds current source.
Each process runs 3 attempts by default. `LITEDB_REPRO_ATTEMPTS` changes that
count. Output records attempts and observed failures; all attempts must pass to
report no reproduction. Unrelated failures exit 20, so they cannot masquerade as
a fix. The process has a 180-second timeout; increase the runner timeout for
larger repeat counts. An intermittent all-pass run is evidence, not proof of a fix.

The ownership assertion runs with the database object still alive. If it fails,
the outer test finalizes the leaked historical object only after recording the
failure, then rethrows it. This prevents Windows temporary-file deletion from
hiding the original exclusive-open failure.
