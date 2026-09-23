# Release notes: bounded memory management

## Index ordering migration (format v11)

New files use v11. Writable opens automatically migrate v8/v9/v10 indexes for corrected
nested collation, unsigned ObjectId, canonical document and exact numeric ordering.
Read-only files needing migration must first be opened writable. Unique-key
collisions abort before changing data or WAL. Computed/multikey keys regenerate
from documents; scalar member-path indexes reuse their pages. Old readers reject
v11, and interrupted migrations resume through WAL recovery. Migration can require
substantial temporary/WAL space. See [the compatibility contract](collation-runtime-compatibility.md).
Finite `LIMIT_SIZE` is checked before promotion; insufficient budgets leave legacy
files unchanged. Computed-index pages are reused during regeneration. An explicit
`index migration limit size` connection option raises the budget atomically with
a successful migration and allows retrying previously interrupted v11 migrations.

## Breaking change: exact numeric comparison

Mixed numeric types (`Int32`, `Int64`, `Double`, `Decimal`) now compare by their
exact represented values, as MongoDB does. Previously a double was rounded to
decimal first. A double and a decimal are therefore equal only when the double
holds that exact value: `0.5 = 0.5m` and `1.0 = 1m` still match, but binary64
`0.1` (0.1000000000000000055511151231257827…) is greater than decimal `0.1m`.
Every query path now agrees: index seeks, full scans, `DeleteMany`/`UpdateMany`
predicates, in-memory expressions, `GROUP BY`, and `BsonValue` equality and hash codes.
Previously an index seek could disagree with a full scan.

A query that compares a decimal field with a fractional double no longer matches.
For example, `$.price = 19.99` (the literal `19.99` is a double) does not find a
stored `19.99m`. Compare with a decimal instead:

```csharp
col.Find(BsonExpression.Create("$.price = @0", 19.99m)); // decimal parameter
col.Find(Query.EQ("price", 19.99m));
col.Find("$.price = DECIMAL(19.99)");                    // rounds the literal to decimal
```

Use one numeric type per field, or convert explicitly, when a field holds values of
both types.

## Data-page and WAL checksums (#2935)

New files use format v10. Writable v8/v9 opens automatically recover the legacy
WAL and durably publish v10 before accepting writes. Existing pages gain
checksums lazily when written; cutover only rewrites the header.
Read-only legacy opens preserve their files. Older engines refuse v10, so keep a
backup before writable open if backward compatibility is required.

Recovery validates salted WAL frame checksums, transaction page counts/digests,
and commit order. Missing, torn, or stale frames discard the incomplete transaction
and its dependent tail; `$database.recoveryDiscardedWalBytes` reports excluded bytes
and `$database.recoveryInvalidWalTail` distinguishes invalid/partial frames from
intact unconfirmed tails. Shared mode retains the report across internal reopenings.
Checkpointed data pages also have checksums and fail explicitly when damaged.
Checkpoint uses a temporary header journal to recover torn header writes.
Automatic conversion keeps verified legacy redo until v10 publication is durable,
using 32 KiB of temporary WAL, independent of database size (plus the encryption
preamble). `$database.checksumCoverage` distinguishes Mixed from Complete
protection. Explicit rebuild completes coverage; cold legacy pages remain
unchecksummed until written or rebuilt.
Plain and encrypted files use the same validation. See the
[format, conversion, and recovery details](page-and-wal-checksums.md).

## `BsonValue` CLR collection compatibility

`new BsonValue(object)` now supports CLR arrays, lists, and dictionaries as
mutable BSON containers. `AsArray`, `AsDocument`, JSON conversion, comparison,
and database persistence all use one stable BSON container for each wrapped
value.

Collection inputs are copied during construction. Later changes to the source
list or dictionary are therefore not reflected in the `BsonValue`. Dictionary
keys use case-insensitive BSON document semantics; when source keys differ only
by case, the last value enumerated wins.

`BsonValue.GetHashCode()` now agrees with `Equals()`: numerically equal values
(`1`, `1L`, `1.0`, `1m`), binary values with the same bytes, UTC-equivalent
dates, and arrays/documents with equal content share a hash code. Collection
hash codes are content-based, so they change when the collection is mutated.

Comparing a double that decimal cannot hold (NaN, infinity, or a magnitude of
2^96 and above) against an `Int32`, `Int64` or `Decimal` used to throw
`OverflowException` from `CompareTo`/`Equals`. Such a double now orders by its
sign (NaN and negative values first) and never compares equal.

## Stream ownership change

Streams supplied through `EngineSettings.DataStream`, `LogStream`, and
`TempStream`, or through the `LiteDatabase(Stream)` constructor, remain open
after the database is disposed. The caller owns these streams and must dispose
them after disposing the database. Engine-created file and temporary streams
are still closed by the engine.

Applications that previously relied on database disposal to close supplied
streams should add explicit stream disposal, for example:

```csharp
using (var stream = File.Open("data.db", FileMode.OpenOrCreate))
using (var database = new LiteDatabase(stream))
{
    // Use the database. It is disposed before the caller-owned stream.
}
```

## Transaction and WAL cleanup

Repeated safepoints reuse the transaction's existing unconfirmed WAL page
positions. A confirmation page is always appended after the transaction's
other pages, retaining the existing recovery format and ordering. Readers of
committed versions continue to use separate, immutable WAL positions.

Transaction cleanup errors no longer prevent removal from the transaction
registry, clearing the thread's transaction slot, or releasing its transaction
lock. Explicit cleanup also releases collection locks on their owning thread.

Transactions no longer run managed cleanup on the finalizer thread. The
monitor already retains registered transactions and releases their pages and
readers during explicit engine disposal. Applications must still finish their
transactions on the originating thread and dispose their databases; garbage
collection does not release abandoned thread-affine locks in a live engine.

## Durable commits (#2818)

Every committed transaction is now synced to the storage device before the
commit returns, so acknowledged commits survive power loss on storage that can
sync. Storage that rejects the sync (some network shares and virtual file
systems, #2242) falls back to the earlier behaviour; `$database.durableLogFlush`
reports which one is in effect. This costs about one
device sync per commit (about 1 ms on NVMe, far more on hard disks and network
volumes); batched transactions and `InsertBulk` are unaffected. Set
`durable commits=false` (`DurableCommits = false`) to restore the earlier
behaviour, where commits survive a process crash but not necessarily a power
loss. See [the trade-off](connection-string-parsing.md#opting-out-of-durable-commits-2818).

## Connection strings and equals signs in filenames

Raw paths such as `data/test=1.db` now work in the string constructors.
A single unknown `key=value`, including `tenant=acme` or a typo such as
`filenam=production.db`, is treated as a filename; this changes the previous
custom-option behavior. Input containing both `=` and `;` is parsed as options
and never falls back to a filename. Use `new ConnectionString { Filename = path }`
for arbitrary paths. See [the parsing compatibility notes](connection-string-parsing.md)
for explicit syntax and integration requirements.
