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
  `reject invalid local time`, `collation`, `memory profile`, `cache size`, or
  `transaction pages`.
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
