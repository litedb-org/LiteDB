# Connection-string parsing compatibility (#2211)

Database paths can contain equals signs: `test=1.LiteDB`,
`data/test=1.db`, and `C:\data=1\test.db` are accepted as filenames.
There is no database format change.

The string constructors of `ConnectionString` and `LiteDatabase` use these rules:

- Input without `=` is a filename.
- Input with both `=` and `;` is parsed as connection options. Parsing errors
  propagate; the input never falls back to a filename. A path followed by
  options, such as `data/my.db;readonly=true`, is invalid: write
  `filename=data/my.db;readonly=true` instead.
- A single `key=value` is parsed as an option if its trimmed key matches a
  built-in name, ignoring case: `filename`, `connection`, `password`,
  `initial size`, `readonly`, `upgrade`, `auto-rebuild`,
  `reject invalid local time`, `durable commits`, `collation`, `memory profile`,
  `cache size`, or `transaction pages`.
- A single unknown `key=value`, including `tenant=acme` or the typo
  `filenam=production.db`, is now a filename. This changes the previous
  custom-option behavior and can select a different database. Multiple custom
  options such as `tenant=acme;region=west` remain available through the indexer;
  they do not supply a database filename.

Set properties to avoid ambiguity for arbitrary paths:

```csharp
using (var db = new LiteDatabase(new ConnectionString
{
    Filename = "password=secret;archive.db",
    Password = "actual database password"
}))
{
    // Use the database.
}
```

Alternatively, explicitly name and quote a filename containing semicolons:
`filename="data/my=1;archive.db";readonly=true`.
Settings-only input remains a connection string with no filename; opening a
database still requires a data source. Recognition of the three memory-setting
names is compatible with the settings added in #2772.

## Rejecting nonexistent local times (#2357)

`DateTime` values are stored as UTC. A Local or Unspecified value inside the
hour skipped by a daylight-saving transition (for example 02:30 on the
spring-forward night) does not exist, and `ToUniversalTime` maps it onto the
following valid hour, so hourly local keys can fail with a confusing duplicate
key error. This remains the default. Set `reject invalid local time=true`
(`ConnectionString.RejectInvalidLocalTime`, `EngineSettings.RejectInvalidLocalTime`)
to make Insert, Update, Upsert and bulk writes throw an `ArgumentException`
instead when a document contains such a value at any depth, including `_id`.
A rejected write inside an explicit transaction rolls that transaction back.
The check uses `TimeZoneInfo.Local`: it never fires on a UTC host, and in zones
that switch at midnight a date-only value can be rejected. Utc, ambiguous,
`MinValue` and `MaxValue` values are accepted and queries are never checked.
The duplicate key error explains this itself when the key is a local time that
collapses with another one around a transition.

Storing UTC values avoids the problem altogether: use `DateTimeKind.Utc` values
and set `db.UtcDate = true` (pragma `UTC_DATE`). Without `UtcDate`, stored
values are converted to local time on read, so UTC keys come back shifted and
look wrong even though they are stored correctly.

## Opting out of durable commits (#2818)

Since 6.0 every committed transaction is synced to the storage device
(`FileStream.Flush(true)` on the log file) before `Commit`, or an auto-commit
write, returns. An acknowledged commit therefore survives power loss and an
operating system crash. This remains the default.

The price is about one device sync per commit. Measured on a local NVMe disk,
200 single inserts take about 210 ms instead of about 7 ms, and 200 single
updates about 210 ms instead of about 4 ms (roughly 1 ms instead of 0.03 ms per
commit); hard disks, network and cloud volumes pay far more per sync. One
transaction that batches 200 inserts, and `InsertBulk`, pay a single sync and
are not measurably slower.

Set `durable commits=false` (`ConnectionString.DurableCommits`,
`EngineSettings.DurableCommits`) to get the behaviour before 6.0 back, for bulk
loads, caches, test suites and other data that can be rebuilt:

- Commits are handed to the operating system and are never synced. They survive
  a crash or kill of the process: reopening the database recovers every
  committed transaction from the log.
- A power loss or operating system crash can lose the most recent commits.
  Checksummed WAL recovery rejects an incomplete transaction and all later
  commits, preserving the preceding valid commits. `$database.recoveryDiscardedWalBytes`
  reports the discarded tail. Do not opt out for data that must survive power loss.
- Checkpoints stay synced: the log is synced once before its pages are copied
  into the data file, and the data file is synced afterwards, so everything
  that has been checkpointed is durable. File creation is synced as before.

The setting applies per open and is not stored in the data file: the file format
is the same for either value. Both settings work with `Direct` and
`Shared` connections, encrypted or not. `:memory:`, `:temp:` and non-file
streams cannot be synced and ignore it. `$database.durableLogFlush` reports
`false` while commits are not synced, either because of this setting or because
the storage rejected the sync request (some network shares).

## Integration with pending parser and serializer changes

[PR #944](https://github.com/litedb-org/LiteDB/pull/944) replaces option parsing.
Keep the path/option classification ahead of that parser and propagate its
errors. Its current rejection of unknown keys conflicts with the custom-option
contract above; integration must preserve custom options or explicitly revise
and document that contract. Run `Issue2211_Tests` with its parsing tests.

[PR #2745](https://github.com/litedb-org/LiteDB/pull/2745) adds serialization.
Serialized options should use explicit keys and quote delimiter-containing
filenames and values. Round-trip tests should include equals signs, semicolons,
passwords, and settings without a filename. Its current early return for an
empty filename cannot preserve settings-only input. Password-redacted output
is for display and must not be used as a lossless connection string.
