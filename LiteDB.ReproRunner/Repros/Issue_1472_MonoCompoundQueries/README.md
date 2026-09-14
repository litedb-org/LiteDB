# Issue 1472: cached compound predicates under Mono

The same workload runs against LiteDB 5.0.2 and current source under pinned Mono
6.12.0.182. Docker is required on Linux; it fetches the official image when absent.
Compilation and database writes occur in an isolated temporary container directory.

```sh
dotnet run --project LiteDB.ReproRunner/LiteDB.ReproRunner.Cli -c Release -- \
  run Issue_1472_MonoCompoundQueries --report issue1472.json --report-format json
```

Eight readers execute the same two-term indexed predicate with independently owned
parameter documents while another worker bulk-inserts 1000 rows. The compound
predicate exercises the shared child-expression parameters in 5.0.2. Mono retains
the Dictionary enumeration behavior needed to reproduce the reporter's exact
`BsonDocument.CopyTo -> IndexCost` exception. All readers use the same parameter
values to isolate that exception from a separate cross-query value race; the
existing .NET regression also covers different per-reader values.

Each attempt checks the exact query IDs and payloads, unchanged caller parameters,
the complete inserted ledger, two reopens, and a recovery write. Workers finish
before disposal. Persistence controls execute before a captured query exception is
rethrown. Only the exact enumeration exception with both reported LiteDB stack
frames counts as proof; assertion failures and unrelated errors exit 20. There are
three attempts per process; every attempt must pass to report no reproduction.
The linked scenario also runs in the ordinary .NET test suite.
