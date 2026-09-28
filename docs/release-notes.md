# Release notes

## Shared mapped reads

Repeated Shared queries on qualified .NET 8+ local filesystems can retain a read-only
snapshot and use mapped status to avoid reopening under the database mutex. Writers
publish version/storage changes and cached readers yield briefly under writer pressure.
Database/WAL formats and commit durability are unchanged by this optimization.

Set `AppContext.SetSwitch("LiteDB.DisableSharedMappedReads", true)` before creating
connections, or `LITEDB_DISABLE_SHARED_MAPPED_READS=1` before startup, to opt out.
`SharedEngine.CoordinationFallbackReason` explains local attachment fallback in
production. The new `-shared-live`, `-shared-state` and `-shared-disabled` files are
ephemeral; do not remove or synchronize them while any participant is alive.
Unix processes with disabled .NET file-sharing locks now reject Shared/Coordinated
participation because both mapped admission and existing leases require those locks.
See [operation, backup, platform and scheduling details](shared-mapped-operations.md).

## Snapshot-aware checkpoints (format v13)

Checkpoint no longer waits for readers. It copies pages no live snapshot still
needs and reuses WAL payload slots that no local or shared-mode reader can reach.
New files keep the v11/v12 creation policy. Existing v11/v12 files promote to v13
at the first checkpoint that retires WAL frames, publishing one header through the
existing WAL-bound header journal; documents are not rewritten. Earlier formats first perform
the migrations below. Released engines and the v12 engine reject v13.

**`Checkpoint()` may leave a nonempty WAL** while readers are open. The data file
alone is a complete backup only when the WAL is empty afterwards. Exclude writers
and checkpoints throughout backup preparation and capture; then either close
readers, checkpoint and verify an empty WAL before copying the data file, or
capture data and WAL together as one filesystem snapshot. Never copy the two live
files independently, and keep a data/WAL pair together.

Retired frames keep durable witnesses (56 bytes each, up to 145 per WAL frame) so
recovery and rebuild still verify complete transactions after slot reuse.
Reclamation reuses WAL capacity but does not shrink the file; long-lived readers
can grow witness metadata and recovery work until a full checkpoint. On storage
that rejects device sync (#2242), snapshot checkpoints still run, but no frame is
retired and no slot reused: the WAL appends as before v13 until a full checkpoint
truncates it. Every retiring checkpoint first syncs the data file and the WAL (and
once the WAL's directory), so such storage is found before any witness depends on it;
storage that stops syncing during the checkpoint keeps the retired frames and publishes
no root once it found out. An engine whose data is a file (opened by it or passed as a
`FileStream`) proves the data file syncs before its first log sync, once per data header
in the process, and syncs the WAL once before it first reuses a slot. On storage that
syncs, a retiring checkpoint costs one extra data and log sync and an engine's first slot
reuse one log sync; the data file proof costs nothing while its header is unchanged. The
process remembers, per path, the header its latest data sync left: a file replaced at that
path with a byte-identical header (a copy of the same checkpoint restored over it) is taken
as that synced file, so whoever replaces a database file must sync it (`File.Copy` does
not); a replacement with any other header is proven again. A
data sync that fails with an I/O error during that proof fails the operation and stops
the engine, as it does in a checkpoint. After a data sync answered "cannot sync", a log
sync waits for the data file (each one retries its sync first and otherwise flushes the log
to the OS cache only), so no log sync makes an emptied or converted WAL durable ahead of
its unsynced backfill or header: commits acknowledged durable before the storage stopped
syncing survive a power loss, also when the WAL alone syncs again. A full checkpoint
empties the WAL on such storage as before #2818, so the WAL stays bounded, and a rebuild or
a 5.x conversion runs as where neither file syncs. A 5.x data file found beside its conversion's WAL (whose
converted header never reached the device) fails to open instead of being replayed. Larger
shared-mode query results stream from a private snapshot protected by a lease
file in `<database filename>-readers/`. All shared participants must run on one
host with working file-sharing locks, use the same mutex naming strategy and this
coordination protocol. Where lease files cannot be created (for example, a
read-only directory), large results stream under the database mutex as before.
Do not mix concurrent direct connections or older shared-mode engines with these
readers. Writes from the thread iterating a streamed result of the same connection
keep one engine open while that thread keeps writing, blocking other writers
meanwhile, as before v13. Unlike before, an idle iteration, a result disposed on
another thread (for example after an `await`), or an exited thread no longer
keeps other threads and processes waiting. This also holds for results that
stream under the mutex because no lease could be registered, and for disposing
the connection on another thread; before, both threw `ApplicationException` and
kept the mutex until the acquiring thread exited. When the last streamed result closes,
the connection checkpoints away the remaining WAL. A shared operation's engine
checkpoints on close only once the WAL holds 50 pages (or CHECKPOINT, if smaller);
disposing the connection checkpoints the rest. This roughly halves the cost of a
small shared write (about 9 ms to 4 ms per update on a local NVMe drive); the WAL
file therefore usually exists while shared connections are open.
On Windows a shared connection also keeps its data and WAL file handles open
between operations (each operation still reads the files afresh), which roughly
halves the cost again: about 4.8 to 3.3 ms per small update and 1.5 to 0.8 ms per
`FindById`. **Behavior change:** while a shared connection is open, and no longer
only during one of its operations, a direct connection or any other opener that
denies write sharing (`FileShare.Read`, e.g. `File.OpenRead`/`File.ReadAllBytes`)
is refused with a sharing violation. `File.Copy` shares write access and still
works. Close shared connections before opening the file in direct mode. Volumes
without POSIX delete semantics (FAT, exFAT, many network shares) keep opening the
files per operation, with the previous sharing behavior.
Rebuild requires shared readers to be closed. See
[snapshot checkpointing](mvcc-checkpoint.md) and
[the retirement format](mvcc-retirement-format.md).

## Compact storage (v12)

Auto creates v12 files; existing v11 files promote on their first beneficial compact
write using the synced header recovery journal. Earlier formats first perform the
checksum and index-ordering migrations below. No document scan occurs merely to
enable compact writes. Legacy mode and explicit BSON rebuild use v11, retaining
checksums and corrected indexes; they cannot downgrade to released v8/v9 engines.
See `compact-document-storage.md`.

## Index ordering migration (format v11)

New files use v11. Writable opens automatically migrate v8/v9/v10 indexes for corrected
nested collation, unsigned ObjectId, canonical document and exact numeric ordering.
Read-only files needing migration must first be opened writable, or opened with
`legacy index scan=true`, which leaves them unchanged and answers queries with
full scans instead of their unmigrated indexes. A caller's data or log stream that
cannot be written (`LiteDatabase(Stream)` or `EngineSettings` streams) does the latter by
itself when opening would change a stream (recovery, repair, migration or conversion):
explicit transactions still work, but write operations are rejected. Otherwise it opens
as before; it never checkpoints into, marks or promotes such storage, and keeps writes
in the log. An engine opened read-only writes nothing to its streams, not even the
invalid-state mark after it finds damage (5.0.21 wrote that mark into a writable caller
stream, which is never rebuilt). Damaged data that prevents migration
fails the open with the damaged collection named and marks the file for rebuild;
with `auto-rebuild=true` the same open rebuilds it, keeping the readable fields of a
damaged document as 5.x did, after every complete document: a part that repeats the
`_id` of a complete document or a key of a unique index is listed in `_rebuild_errors`
instead of failing the rebuild. A kept part reads like a complete document, so it is
also listed there by `_id`; a unique index gets its missing key as null, and a later
document without that field then conflicts with it. When the rebuild's repeated open
fails with an exception other than a `LiteException`, that exception keeps its type
and carries the damage in `Data["LiteDB.RebuildCause"]`. A vector index whose section a
release without vector support (5.0.21) displaced when it rewrote the index list has no
metadata: read-only opens work, a writable open reports it as damage, and the rebuild
drops only that index. Conversion first drains a legacy WAL completely. While another
shared connection may still read it (a reader's lease, or a reader registry that cannot
be inspected) the open is refused with `LOCK_TIMEOUT` before any document is validated,
changing neither file; an unreadable registry keeps refusing until it can be read. A committed legacy
WAL page that is not a page of its type (an unknown type, or page 0 that is not the
header) or that names a page beyond both files and every page a committed header counts,
and a committed header of another database (another creation time), fail the open with
`INVALID_DATABASE`, changing neither file (5.0.21 wrote it over the header, that far into
the data file, or replayed the other database into it). The creation time identifies the
database that was created, not each copy of it: a log left beside a fresh copy of the same
seed file still passes, as in 5.0.21. Unique-key
collisions abort before changing data or WAL. Computed/multikey keys regenerate
from documents; scalar member-path indexes reuse their pages after keys that
released updates left stale (for example `19.99` for a stored `19.99m`, including
primary keys) are regenerated from their documents. Old readers reject
v11, and interrupted migrations resume through WAL recovery. Migration can require
substantial temporary/WAL space. See [the compatibility contract](collation-runtime-compatibility.md).
Finite `LIMIT_SIZE` is checked before promotion for migrations that must grow the
file; insufficient budgets leave legacy files unchanged. Computed-index pages are reused during regeneration. An explicit
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

A WAL write that may have left a torn frame stops the engine before the WAL writer is
released, whatever the exception type: a failed overwrite of a slot, an append whose
truncation failed, and a checkpoint that fails while writing the WAL. 5.0.21 rolled back
and continued after a non-I/O failure; a later commit could then land behind the torn
frame and be lost at recovery. A caller stream other than a `MemoryStream` is flushed
after each write, under the lock its readers take: a buffering one (a `BufferedStream`, a
`FileStream` with a large buffer) held frames the next write or a reader's seek wrote on,
where a failure tore a frame the writer never heard of. That is one `Flush()` per page
write, data and log: a custom stream whose `Flush()` syncs or uploads now pays that per
page instead of per batch. A caller stream must not replay a write that failed at another
position: a `BufferedStream` keeps a buffer whose write failed and writes it again at its
next access, at the inner stream's current position, so over an inner stream that advanced
past the bytes it stored before failing (a `FileStream` does not) the page lands shifted
over the next one, as in 5.0.21. A transaction
whose safepoint failed to write its pages can only roll back: later reads and writes in
it throw "can only be rolled back", and `Commit` rolls it back and throws.

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
systems, #2242) falls back to the earlier behaviour for commits, checkpoints and
format conversion; `$database.durableLogFlush` reports which one is in effect. A
sync that fails with an I/O error still stops the engine before data is overwritten.
On Linux and macOS this requires LiteDB's own device sync: released .NET runtimes
lose every `fsync` error in `FileStream.Flush(true)` (dotnet/runtime#124725), which
had hidden EIO and unsupported-sync answers alike. File handles are now synced with
`fsync` (`F_FULLFSYNC` on macOS, falling back to `fsync`), so such storage reports
`durableLogFlush=false` and an EIO stops the checkpoint. Encrypted databases on
such storage now open only read-only (a writable open or creation fails), as they
already did on Windows: the encrypted preamble requires a successful sync and has no fallback. This costs about one
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
