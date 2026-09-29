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
`BufferedStream`, a `FileStream` with a large buffer) and write them on at its next write,
seek, length query or flush, where a failure can tear one: a failure while it may still hold
a frame of the batch stops the engine, until the batch's final flush succeeded. A reader's
access (a seek) first flushes what the stream holds for the writer, under the lock the
wrappers share; any failure there fails the read as a write failure of that file and is
handed to the writer, whose batch fails. Before, the reader's seek tore a frame the writer
never heard of. The engine still flushes a caller stream once per WAL batch and per sync, not
per page. A caller stream must not replay a write that failed at another position.

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

## Coherent WAL durability and recovery protocol

The durability rules follow the maintainer's decisions in
[decisions/durability-policy.md](decisions/durability-policy.md). This section supersedes the
terminal stop of "WAL write and transaction failure containment" above.

- **A durable commit fails loudly.** With durable commits (the default), before an engine's first
  WAL batch the log file and its directory are proven to sync, once per log path per process, so
  storage that syncs pays it once. On storage that answers "cannot sync" (EINVAL, ENOTSUP, EROFS;
  some network shares and virtual file systems, #2242), or a WAL directory that cannot be synced
  or opened to be synced (EACCES, EPERM), the commit throws an `IOException` ("This commit was not
  written: ...") before it writes a frame. A log that stops syncing later fails the commit that
  finds out, whose outcome is then unknown. Set `durable commits=false` to use such storage:
  commits then reach the OS cache only, and "cannot sync" is not a failure there. "Cannot sync"
  and a failed sync (an I/O error) stay distinct everywhere: the first degrades or refuses, the
  second is recorded.
- **`CommitOutcome`.** A commit that fails carries its outcome in
  `Exception.Data["LiteDB.CommitOutcome"]`, whatever the exception type: `"NotCommitted"` only for
  a provable abort (refused before its first frame, failed before its confirmation with nothing
  torn left behind, or its confirmation's failed append truncated away and that truncation
  synced), `"Unknown"` otherwise (a later open may recover it). After `"Unknown"`, retry only
  idempotent writes, or check after reopening.
- **The WAL is removed only behind a data sync.** The WAL, its header journal and a legacy header
  backup are removed, and retired WAL slots cleared, only after a data sync that covered every data
  write succeeded. Every checkpoint that writes, and every format promotion, first syncs the data
  file (one more data sync on storage that syncs) and writes nothing while that sync fails. While
  only the data file cannot sync, log syncs go on and commits stay durable in the WAL, which is
  kept and grows. An engine also syncs the data file once before its first durable commit, best
  effort (once per data header in the process, once per shared connection).
- **`wal limit`** (`EngineSettings.WalLimit`, default 1 GiB) bounds that kept WAL: past it a write
  that starts throws an `IOException` while reads keep working. Each refused write first retries the
  log and data syncs, so writes resume once the storage syncs, and the next checkpoint drains the
  WAL. A transaction already running may commit past the limit.
- **Header frame.** Every non-volatile checksummed WAL generation starts with a header frame, a copy
  of the data header made durable by its first commit's log sync. An open restores a data header a
  power loss left unwritten (a data file left empty, shorter than a page, or with header sectors
  that are the frame's or zeros; plain and encrypted) from it, and refuses to initialize a database
  over a WAL whose data file is gone, lost pages the frame names, or whose frame cannot be read
  (torn, or a wrong password); the rebuild's reader does the same. A WAL written without a header
  frame opens as before. The header frame is a copy of the header only, not a backup of every data
  page: the supported lost-page fault model is unchanged.
- **Overwrite barriers, in both commit modes.** A checkpoint's backfill, a format promotion, a legacy
  conversion and the invalid-state mark overwrite the data file only behind a header journal and
  WAL that are on the device. Behind a log whose latest sync answered "cannot sync", they refuse
  before the journal is written: with `durable commits=false` a checkpoint writes nothing and keeps
  the WAL (up to `wal limit`), a compact write stays BSON and a conversion or promotion opens
  read-only with both files unchanged (before, they proceeded without a power-loss guarantee); with
  durable commits the barrier throws "The log file cannot sync". An open that recovers a header from
  its journal writes it back (the same bytes) before the sync that retires the journal, since Linux
  may have marked pages a failed sync could not write back clean ("fsyncgate").
- **Read-only continuation.** A write or sync failure (a torn WAL write, a failed flush, a checkpoint
  or promotion whose data sync fails after it wrote, disk full, a data sync that fails with an I/O
  error where a write at the WAL limit, a rebuild or a `$database` read tried it) is recorded: file,
  operation, error, time and whether the log file was kept. The engine then reopens read-only from
  the files as they are: reads keep working, every write throws an `IOException` carrying the
  recorded failure before it changes anything, an explicit transaction the failure ended throws at
  `Commit`, and the engine never retries a sync on the handle that failed or auto-rebuilds. Only a
  new open writes again. An in-memory or temporary database stays closed. A writable open that would
  have to convert a 5.x file, migrate its indexes, or repair or retire a header journal while the
  data file cannot sync opens read-only instead, and a rebuild there is refused and leaves the
  database unchanged. A refusal found before anything was written is not a failure.
- **`$database` fields.** `readOnly`, `readOnlyReason`, `writeFailure` (`{file, operation, error,
  time, walKept}` or null), `walKept`, `walLimit`, and 64-bit `dataFileSize`/`logFileSize`. A shared
  connection reads `$database` from its operation engine, not from a read-only snapshot.
- **Failure boundary of sync helpers (#3052).** A sync helper that runs after an earlier admission
  check (`$database.walKept`'s syncs, a checkpoint's syncs, a promotion, a WAL batch) rechecks the
  stopped and recorded-failure state once it holds the lock that orders its sync, and records a
  real I/O failure of its own sync before it releases that lock, so a writer, checkpoint or helper
  waiting behind it syncs and writes nothing more. "Cannot sync" is never recorded this way.
- **Unverified syncs (an explicit exception).** Without a C library on Unix the runtime's
  `Flush(true)` loses sync errors: syncs are attempted and commits acknowledged, `durableLogFlush`
  reads false, and a checkpoint's barrier counts such a sync as synced. Nothing is recorded as a
  failure, and such a log never reuses WAL slots. A durable-commit open logs it once.

Not in this layer: the read-only reopen replays the files as they are, so after a commit whose
outcome is `"Unknown"` it may show that commit; a shared connection does not keep a failure for
its later operations (each operation's fresh engine proves the log again); before an engine
first reuses a retired WAL slot it still syncs the data file and the log once. The WAL frames of
a commit whose log sync failed stay in the OS cache only, and a later process may build on them
([#3048](https://github.com/litedb-org/LiteDB/issues/3048)); a rebuild whose recovery marker or
directory cannot sync installs its replacement without a power-loss guarantee
([#3049](https://github.com/litedb-org/LiteDB/issues/3049)).
