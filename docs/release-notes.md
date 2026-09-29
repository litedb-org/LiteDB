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
that rejects device sync (#2242) no frame is retired, and an engine that found it out
reuses no slot. Every retiring checkpoint first syncs the data file and the WAL (and once
the WAL's directory), so such storage is found before any witness depends on it; storage
that stops syncing during the checkpoint keeps the retired frames and publishes no root
once it found out. Reusing a slot syncs nothing: a slot is free only while its witness
root is on the device (the retiring checkpoint synced it before removing its header
journal; if that sync failed, the next open syncs it before retiring the journal, or
opens read-only), and a clear lost to a power loss leaves a witnessed old frame that
recovery skips. On storage that syncs, a retiring checkpoint costs one extra data and
log sync, and slot reuse costs none, also per shared-mode operation.

The durability rules follow the maintainer's decisions in
[decisions/durability-policy.md](decisions/durability-policy.md):

- **The WAL is removed only behind a data sync.** The WAL, its header journal (the log's
  recovery copy of the data header) and a legacy header backup are removed only after a data
  sync that covers every write the engine made to the data file succeeded: the OS could write
  an emptied WAL back ahead of the backfill, and no engine can know whether an earlier one,
  maybe of another process, synced its frames. Every checkpoint that writes first syncs the
  data file (one more data sync per such checkpoint on storage that syncs; a retiring
  checkpoint's proof already is that sync) and writes nothing while that sync fails, in every
  engine, including a restart or a shared-mode operation. A WAL the engine keeps in memory
  (`:memory:`, `:temp:`, `LiteDatabase(Stream)` without a log stream) survives no power loss
  and is still emptied.
- **A commit that cannot be made durable fails loudly** (with durable commits, the default;
  see "Durable commits" below). It throws before it writes a frame and is never acknowledged
  as non-durable.
- **Commits stay durable in the WAL while only the data file cannot sync.** Log syncs no
  longer wait for the data file; checkpoints write nothing, so the WAL keeps every commit and
  grows. `$database.walKept` reports it (a writable engine that has not seen a data sync
  succeed tries one first; a read-only engine reports what its connection's engines found),
  with `logFileSize` (now 64-bit, like `dataFileSize`) and `walLimit`. Past the WAL limit
  (`wal limit`, `EngineSettings.WalLimit`, 1 GiB by default) a write that starts throws an
  `IOException` while reads keep working; each refused write retries the sync that failed, and
  writes resume once it succeeds (the next checkpoint drains the WAL). The WAL carries the data
  header its frames depend on (its WAL salt, version and creation time): every WAL now starts
  with a header frame, a copy of the data header made durable by the first commit's log sync.
  An open restores a data header the device lost (a data file left empty, shorter than its
  header, or with a torn header) from it, and refuses to initialize a new database over a WAL
  whose data file is gone or lost pages the frame names. An engine still syncs the data file
  once before its first commit, best effort (once per data header in the process, once per
  shared connection); no commit depends on it. On storage where neither file syncs,
  opted-out commits keep the WAL the same way (before, it was emptied as before #2818).
- **Failures are sticky.** A write or sync that failed (a torn WAL write, a failed flush, a
  checkpoint or promotion whose data sync fails after it wrote, disk full, also at a
  safepoint of a large transaction, and a data sync that fails with an I/O error where a
  write at the WAL limit, a rebuild or `$database` retried it) is recorded: file, operation,
  error, time, and whether the log file was kept. The operation that hit it throws when it was
  the caller's own (a commit, an explicit `Checkpoint()`); an automatic checkpoint after a
  successful commit, `Dispose` and a `$database` read do not throw. The engine then continues
  read-only until the database is reopened: reads keep working, every write throws an
  `IOException` with the recorded failure (its inner exception) before it changes anything, so
  does an explicit `Checkpoint()`, an explicit transaction the failure ended throws at
  `Commit` (or at the next `BeginTrans` on its thread), and `$database.writeFailure` reports
  the record without a write (`durableLogFlush` reads false after a failure on the log). A
  shared connection keeps it for its later operations until it is reopened; two shared
  connections do not share it. The read-only engine never rebuilds (`auto-rebuild`) and never
  retries a sync on the handle that failed. Before, the engine closed and every later call,
  reads included, threw. A commit that fails carries its outcome in
  `Exception.Data["LiteDB.CommitOutcome"]`, whatever the exception type: `"NotCommitted"` when
  its confirmation cannot be in the log (refused before it wrote, failed before its confirmation
  with nothing torn left behind, or its confirmation's failed append truncated away and the
  truncation synced), `"Unknown"` when it may be (a later open may recover it). The read-only
  engine that replaces the failed one shows only the commits acknowledged before the failure,
  while the files are exactly as the failure left them; a later open lets the files decide. An
  open that recovers a header after a failed sync writes it back before the sync that retires
  its journal, since Linux may have marked the failed pages clean. A refusal found before anything was written (a format promotion whose
  data file cannot sync, a write past the WAL limit) is not a failure: it throws to the
  operation's caller (an automatic checkpoint does not), and the engine keeps writing.
- **Opens that would need a data sync open read-only.** A writable open that would first
  have to convert a 5.x file, migrate its indexes, or repair or retire a header journal while
  the data file cannot sync opens read-only instead (a refusal found before the open wrote
  anything changes neither file; one found later leaves what it wrote, which the read-only
  open and the next writable open recover): reads work (indexes in the old order are scanned,
  as with `legacy index scan=true`), explicit transactions are accepted, and every write
  throws an `IOException` naming the cause. `$database.readOnly` and
  `$database.readOnlyReason` report it without a write; a shared connection reads
  `$database` from its own operation engine, not from a read-only snapshot (which also
  reported `readOnly` on a writable connection). Once the data file syncs, the next open
  converts or repairs the file. A rebuild there is refused with an `IOException` and leaves
  the database open and unchanged (5.0.21 rebuilt there without a power-loss guarantee; it had
  no format conversion). A format promotion (a compact or vector write, an index migration,
  or the first retiring checkpoint, that raises the file version) keeps its header journal the
  same way: it first syncs the data file (one more data sync per promotion). While the data
  file cannot sync, a compact write stays BSON and an index migration opens read-only. A vector
  write to a file older than the vector format (a 5.x file, which such an open cannot convert)
  is refused with an `IOException`, and the engine stays usable: reads keep working, and
  nothing is recorded as a failure.

A data sync that fails with an I/O error fails the operation, in both modes. Encrypted streams
opened to read no longer sync their file. A 5.x data file found beside its conversion's WAL (whose
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
frame and be lost at recovery. A caller stream other than a `MemoryStream` may hold frames
after their write returned (a `BufferedStream`, a `FileStream` with a large buffer) and
write them on at its next write, seek, length query or flush, where a failure can tear
one: a failure while it may still hold a frame of the batch stops the engine, until the
batch's final flush succeeded. A reader's access (a seek) first flushes what the stream
holds for the writer, under the lock the wrappers share; that flush only writes, so any
failure there fails the read as a write failure of the file (the engine continues
read-only) and is handed to the writer, whose batch fails. Before, the reader's seek tore a
frame the writer never heard of. The engine still flushes a caller stream once per WAL
batch and per sync, not per page: a custom stream whose `Flush()` syncs or uploads pays that
per batch, plus, while other threads read it during a batch or checkpoint, at most one flush
per page written. A caller stream must not replay a write that failed at another
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
commit returns, so acknowledged commits survive power loss. A commit that cannot be made
durable fails loudly instead of being acknowledged: on storage that rejects the sync (some
network shares and virtual file systems, #2242), a commit throws an `IOException` ("This
commit was not written: the log file cannot sync ...") before it writes a frame, and the
engine then continues read-only (see "Failures are sticky" above). So does a commit whose
WAL directory cannot be synced, or opened to be synced. Before an engine's first commit it
proves, once per log file per process, that the log and its directory sync; storage that
syncs pays that once. A log that stops syncing later fails the commit that finds out, whose
outcome is then unknown (its frames reached the operating system). Set
`durable commits=false` to use such storage: commits then reach the OS cache only and a
"cannot sync" answer is not a failure. No in-place overwrite of the data file goes on behind a
log that cannot sync, in either mode: with `durable commits=false` a checkpoint writes nothing
and keeps the WAL (up to `wal limit`), a compact write stays BSON and a conversion opens
read-only, until the log syncs again (before, they proceeded without a power-loss guarantee).
`$database.durableLogFlush` reports whether commits are
made durable. A sync that fails with an I/O error fails the operation in both modes, before
data is overwritten.
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
