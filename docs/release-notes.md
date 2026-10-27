# Release notes

## Same-thread transaction writes (Phase 5 migration note)

When one `LiteDatabase` has a legacy explicit transaction open, a write through
another facade for the same file on that transaction's thread now fails
immediately with `LiteException.SAME_THREAD_TRANSACTION`. Finish the transaction
on its owning thread before using the peer facade. Phase 4 will introduce
thread-independent transaction handles for coordinated cross-thread completion.
Reads keep their existing behavior, and legacy transactions remain thread-bound
across `await` continuations.

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
can grow witness metadata and recovery work until a full checkpoint. On log
storage that rejects device sync (#2242), snapshot checkpoints and retirement
still run, but reclaimed slots are never reused: the WAL appends as before v13
until a full checkpoint truncates it. Larger
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
full scans instead of their unmigrated indexes. Unique-key
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

## Legacy WAL conversion (5.x files)

Conversion first drains a legacy WAL completely. While another shared connection may still
read it (a reader's lease, or a reader registry that cannot be inspected) the open is refused
with `LOCK_TIMEOUT` before any document is validated, changing neither file; an unreadable
registry keeps refusing until it can be read, and the file converts once the reader is gone.
Before, the conversion checkpointed only what the readers allowed and then emptied the WAL
anyway, losing the transactions 5.0.21 had committed there.

Legacy WAL pages carry no checksum, so their replay is validated. A committed legacy WAL page
that is not a page of its type (an unknown type, or page 0 that is not the header) or that
names a page beyond both files and every page a committed header counts, and a committed
header of another database (another creation time), fail the open with `INVALID_DATABASE`,
changing neither file (5.0.21 wrote it over the header, that far into the data file, or
replayed the other database into it). Committed pages that concurrent 5.x writers allocated
above the data file's last page stay valid, and a torn tail after the last commit is ignored
as before. The creation time identifies the database that was created, not each copy of it: a
log left beside a fresh copy of the same seed file still passes, as in 5.0.21 (#3043 tracks
the ambiguous ownership cases). A 5.x data file found beside its conversion's WAL (whose
converted header never reached the device) fails to open instead of being replayed.

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
systems, #2242) falls back to the earlier behaviour for commits, checkpoints and
format conversion; `$database.durableLogFlush` reports which one is in effect. A
sync that fails with an I/O error still stops the engine before data is overwritten.
On Linux and macOS this requires LiteDB's own device sync: released .NET runtimes
lose every `fsync` error in `FileStream.Flush(true)` (dotnet/runtime#124725), which
had hidden EIO and unsupported-sync answers alike. File handles are now synced with
`fsync` (`F_FULLFSYNC` on macOS, falling back to `fsync`), so such storage reports
`durableLogFlush=false` and an EIO stops the checkpoint. Encrypted databases on
such storage now fail when their WAL is created, as they already did on Windows:
the encrypted preamble requires a successful sync and has no fallback. This costs about one
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

## WAL write and transaction failure containment

A WAL write that may have left a torn frame stops the engine before the WAL writer is
released, whatever the exception type: a failed overwrite of a transaction's own slot, an
append whose truncation failed (the original write failure is reported, not the cleanup's),
an append while a header journal is outstanding (its cleanup never truncates the journal),
and a checkpoint or file format promotion that fails while it holds the WAL writer. 5.0.21
rolled back and continued after a non-I/O failure; a later commit could then land behind the
torn frame and be lost at recovery. A commit already waiting for the writer now finds the
engine stopped.

A caller stream other than a `MemoryStream` may hold frames after their write returned (a
`BufferedStream`, a `FileStream` with a large buffer) and write them during its next write,
seek, length query, or flush. If a failure occurs while the stream may still hold a frame
from the batch and the batch's final flush has not succeeded, the engine stops permanently. A reader's
access (a seek) first flushes what the stream holds for the writer, under the lock the
wrappers share; any failure there fails the read as a write failure of that file and is
handed to the writer, whose batch fails. Before, the reader's seek tore a frame the writer
never heard of. The engine still flushes a caller stream once per WAL batch and per sync, not
per page. A caller stream must not replay a write that failed at another position.

A successfully truncated unconfirmed append does not by itself make the WAL unusable;
the original failure still propagates. I/O failures handled by commit or rollback still
stop the engine under the normal operation-level policy.

The stop is terminal. A failure that reaches the commit or rollback path is recorded (file,
operation, error, time, whether the log file was kept) before the stop; a torn WAL append,
checkpoint or promotion failure stops the engine without a record in this change (recording
for those paths arrives with the durability protocol change). After the stop the engine
performs no further write or sync on its handles, no close-time checkpoint runs, and every later call, reads included,
throws `IOException("Engine closed after an I/O failure ...")` carrying the original failure
as its inner exception. Dispose and reopen the database: recovery shows exactly the commits
acknowledged before the failure.

A transaction whose safepoint failed to write its pages can only roll back: later reads and
writes in it throw "can only be rolled back", and `Commit` rolls it back and throws. A
`$dump` or `$page_list` read inside an explicit transaction that pins one of the
transaction's own WAL slots no longer fails the next safepoint or commit (which used to stop
the engine and mark the data file invalid): the new version is appended and the pinned slot
stays readable.

## Caller streams: stored settings and non-writable streams

`new LiteDatabase(stream)` without a log stream keeps its WAL in memory, so every
commit is checkpointed into the caller's stream. This is now an engine setting of
the open only: the stored `CHECKPOINT` pragma is no longer rewritten to 1 by the
constructor and restored by `Dispose` (a file opened by name afterwards keeps its own
checkpoint size, also when the stream-based instance was never disposed). `Dispose`
can be called twice, and disposing with an open explicit transaction rolls it back
and releases the engine's use of the caller-owned stream without disposing it.

A caller's data or log stream that cannot be written (`LiteDatabase(Stream)` or
`EngineSettings` streams, e.g. a `FileStream` opened with `FileAccess.Read`) opens as
before while opening changes nothing. When opening would have to change a stream
(recovery, header-journal repair, trimming a partial page, index migration or
checksum conversion, e.g. any 5.0.21 file) it opens read-only with `legacy index
scan` instead, as 5.0.21 read such streams: explicit transactions still work, write
operations are rejected. Such storage is never checkpointed, promoted to compact
storage, marked for rebuild or rebuilt (`Checkpoint()` and `Rebuild()` return 0, as
in 5.x; a checkpoint of a non-empty caller log throws `NotSupportedException`), and
the caller's `EngineSettings` instance is not modified. A non-writable data stream
without a caller-supplied log stream rejects writes with `IOException` before they
are acknowledged, including when opening needs no changes. Explicit transactions
that only read remain available. For writable sessions over non-writable data,
supply a writable log stream and retain it alongside the data: commits stay in that
log and reopening requires both streams. Durability promises for other
custom streams are unchanged (#3044).

## Legacy collection pages and vector metadata

A collection from which a released 5.x engine (5.0.21) dropped an index stays writable after
the writable open migrates it. 5.0.21 left the tail of the dropped index entries after the
collection page's index list, and this version read those bytes as vector metadata: every
insert then failed with "request page must be less or equals lastest page in data file", after
the file had been converted, so 5.0.21 could not open it anymore either. The engine now reads
a page's vector section only when the page lists a vector index, and keeps it only when it decodes and
names exactly those indexes. A vector index whose section a release without vector support
(5.0.21) displaced when it rewrote the index list has no metadata: read-only opens work, a
writable open reports it as damage (`INVALID_DATAFILE_STATE`), and the rebuild drops only that
index, listing it in `_rebuild_errors` instead of rebuilding it as an ordinary index. Vector
indexes written by 6.0.0 prereleases keep their metadata through migration and rebuild.

## Shared mode on Unix: long and non-ASCII database paths

On Linux and macOS, `Connection=shared` now opens databases whose URI-escaped
path would exceed the runtime's 255-character named-mutex limit, for example a
directory named with a few dozen Cyrillic or CJK characters. Previously every
shared open of such a path threw `ArgumentException` from the `Mutex` constructor;
5.0.21 opened them. With the default and `UriEscape` strategies, a Unix name is
hashed (`sha1-` + SHA-1 of the lower-cased full path) only when the escaped name
plus the longest mutex suffix (`.Turn.Mutex`) would exceed that limit; escaped
names of up to 244 characters keep their existing identity, and Windows naming is
unchanged. Earlier prereleases without the `.Turn` mutex could open names of 245
to 249 characters; running them together with this version on such a path is
unsupported.
