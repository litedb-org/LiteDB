# Issue 2357: distinct hourly IDs collide across DST

The reporter's attached source and `NewData.json` were inspected with `gh`.
The JSON contains 480 unique timestamps without a timezone suffix, from
`2006-03-23T00:00:00` through `2006-04-11T23:00:00`. It includes both 02:00
and 03:00 on April 2. The source maps that field with `[BsonId]` and calls
`InsertBulk`; Newtonsoft's ordinary parsing leaves these values `Unspecified`.

The earlier xUnit test changed the IDs to UTC, which removes the reported DST
collision. It is retained as a control. This process repro generates the exact
480 wall-clock timestamps with independent ordinal/proof/payload fields instead
of redistributing the reporter's application, database, or financial values.

Three separate child processes establish the boundary:

1. Unspecified IDs in `Etc/UTC`: all records must survive storage and reopen.
2. UTC IDs in `America/New_York`: the same control must pass.
3. Unspecified IDs in `America/New_York`: reproduce the duplicate `_id` at
   `2006-04-02T07:00:00Z` despite 480 distinct source ticks.

BCL checks prove that the requested timezone is active and April 2 at 02:00 is
a nonexistent local hour only in the Eastern-zone processes. Every child has a
20-second execution deadline. Each database starts with a previously committed
record and a unique secondary index. After the bulk attempt, a full scan,
primary-key lookups, and secondary-index lookups check the complete expected
receipt ledger. A failed batch must leave no partial rows. A subsequent insert
and a fresh reopen must preserve the baseline and new receipt.

Only the exact duplicate-key code/date with all controls passing yields exit
`0` and `BUG_2357_CONFIRMED`. Exit `10` requires preserving every wall-clock ID
and payload, or explicitly rejecting the nonexistent local time while retaining
the rollback/recovery guarantees. Any unrelated exception, loss of records, or
failed precondition is exit `20` and cannot be accepted as a fix.

```sh
dotnet run --project LiteDB.ReproRunner/LiteDB.ReproRunner.Cli -c Release -- \
  run Issue_2357_DstDateIds
```

Verified on Linux x64/.NET 8 against the reported package **5.0.17** and current
dev **a50661a9**: both UTC controls pass, then the exact duplicate-date outcome
is reproduced in the Unspecified/Eastern process. This verifies the DST
conversion cause on Linux; it does not claim to run the original Windows Forms
application or .NET Framework 4.8 runtime.
