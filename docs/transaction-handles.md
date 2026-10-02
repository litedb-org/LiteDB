# Explicit transaction handles (v6)

`LiteDatabase.BeginTransaction()` creates one independently owned synchronous
transaction ([#3064](https://github.com/litedb-org/LiteDB/issues/3064)).
`ILiteDatabase.BeginTransaction()` is an extension using the optional
`ILiteTransactionProvider` capability; existing interface implementers need no new
members. Providers without the capability throw `NotSupportedException` before
starting a transaction.

```csharp
using var tx = db.BeginTransaction();
var users = tx.GetCollection<User>("users");
users.Insert(first);
await SomeOtherWorkAsync(); // may resume on another thread; locks stay held meanwhile
users.Insert(second);
tx.Commit();
// Disposing an uncommitted, healthy transaction rolls it back.
```

## Binding

Each collection, query, enumerable and reader obtained through `tx` always executes
within exactly that transaction. After completion it rejects use, including deferred
enumeration; it never falls back to an automatic transaction.

| Call | Transaction used |
| --- | --- |
| Ordinary `db`/collection call | Its automatic transaction, or the calling thread's legacy `BeginTrans` transaction |
| `tx.GetCollection(...)` operations, queries and readers | Exactly `tx`, on any thread |
| `tx.Commit()` / `tx.Rollback()` | Exactly `tx` |
| `db.Commit()` / `db.Rollback()` | Legacy lookup only; never a handle |

Opening a handle does not enlist existing or later `db.GetCollection()` objects, and
`tx.Rollback()` never undoes their writes. Migrating code must change where it gets
its collections, not only the begin/commit method names. Ordinary database objects
used inside a mapper or input callback of a handle operation stay ordinary
operations. Two handles are separate transactions, even on one thread; they are not
nested scopes or savepoints, and conflicting work still obeys collection locks and
their timeout. `BeginTransaction()` is rejected while the calling thread owns a legacy
transaction of the same database. Existing isolation and durability settings are
unchanged; handles add no snapshot-isolation promise.

Internally the engine receives the handle's identity explicitly: a bound call
installs it for its synchronous duration only, composed engine calls carry a
one-use dispatch ticket, and only the deprecated legacy API uses thread-local
lookup. A handle's transaction owns its collection locks and its admission lease,
so a later call on another thread can continue and release them.

## Concurrency

Sequential handoff to another thread is supported, including after the creating
thread exits. Overlapping calls on one handle, and public reentry from its own
callbacks, fail with `InvalidOperationException` before executing and do not abort
the operation in progress. This covers mapping, query execution, enumeration,
reader access, commit, rollback and disposal. The guard detects overlap, not
accidental sequential sharing. One exception: disposing a bound reader or
enumerator on another thread while a call of its handle executes (a `foreach`
ending there) succeeds; the reader is released after that call, at the latest
by the handle's next call or completion, so it never keeps commit refused.

In Direct mode an ordinary call that needs a collection lock or exclusive
maintenance held up by an *idle* handle waits for the existing lock timeout, like any
other conflicting transaction; when the same flow must complete that handle, it then
fails. In Shared mode the wait for the writer mutex has no timeout (see below).

Raw `LiteEngine` calls from a bound callback are rejected before side effects. An
ordinary callback write that needs a collection lock held by the executing handle,
or a rebuild that needs exclusive admission, fails immediately with the existing
lock-timeout error instead of waiting for work that only its own return can release.

## Supported surface

| Backend or API | Handle support |
| --- | --- |
| Direct (file, memory, temporary, caller streams) | Yes |
| Shared, filename-backed | Yes; see below |
| Read-only Direct/Shared | Queries and completion; writes are refused and leave the handle active. A read-only Shared handle still owns the writer mutex while open |
| Shared memory/temporary storage or caller-supplied streams | `NotSupportedException` before admission |
| Shared begin or operation under Windows thread impersonation (including an anonymous token) | `NotSupportedException` before admission or side effects; the handle stays usable from a non-impersonating thread |
| Experimental `CoordinatedEngine`, custom or decorated `ILiteEngine` implementations | `NotSupportedException` before side effects; legacy/ordinary use unchanged |
| Typed/BSON collections, bulk input, queries, Include, vector queries | Yes |
| Index creation/removal, `GetCollectionNames`, `CollectionExists`, `$cols`/`$indexes` | Yes |
| SQL, FileStorage, nested begin, collection drop/rename, checkpoint, rebuild, pragma changes | Not exposed through a handle; drop or rename through the database outside the transaction |
| Other system collections, external query input/output | `NotSupportedException` before mutation |

## Outcome and cleanup

`State` is `Active`, `Committed`, `RolledBack`, `Failed` or `Indeterminate`.
Repeated completion is a usage error; repeated disposal is safe. Commit with an
open bound reader fails before mutation and leaves the transaction active: dispose
the reader and commit again. A disposed bound reader or enumerator throws
`ObjectDisposedException` and leaves earlier writes intact.

A statement failure aborts the handle (there is no statement savepoint) and its
error stays primary; rollback cleanup failures are attached under
`Exception.Data["LiteDB.StatementRollback"]` or `"LiteDB.TransactionCleanupError"`.
Capability and read-only refusals before mutation leave it active. Commit that fails
after it may have published reports `Indeterminate`, never `RolledBack`; a known
committed outcome stays `Committed` even if later cleanup fails. Commit refused
before it could publish (the engine already stopped or closed, the transaction
already ended, or the engine refused it unchanged) throws, rolls back what remains,
and reports `Failed`; it never returns as if it committed. Disposing a healthy
active handle rolls back; if another failure already stopped the engine, disposal
releases the handle without rethrowing that failure.

Completion releases the transaction's locks, Shared writer ownership and storage
references, and unregisters the handle, whether or not it is disposed. Always
dispose handles (`using`): one dropped without completion keeps its collection
locks and its admission lease (which `Rebuild` waits for), and in Shared mode the
writer mutex, until its database is disposed, because the database tracks it.

Disposing the database refuses new handles, waits for a handle call executing on
another thread to return, then rolls back every handle that is still active before
releasing the engine, including with `disposeOnClose: false`. Disposing the database
from inside an executing handle call throws `InvalidOperationException` before any
state changes. The wait is not bounded: lock timeouts bound the call's own lock
waits, but not application code it runs (input iterators, mappers, `ReadTransform`).
A bounded close with deferred cleanup is the session-lifetime contract of
[#3067](https://github.com/litedb-org/LiteDB/issues/3067).

## Shared mode

A Shared handle owns the native writer mutex for its whole lifetime. An internal
holder thread acquires it and opens a fresh storage core for the handle; the
application's threads run the handle's operations on that core in turn. Commit,
rollback or disposal closes the core and releases the mutex on the holder thread,
so other processes and connections then proceed. Completed handles retain no storage
or writer ownership; at most 2 idle holder threads per process and one closed wrapper
per connection may remain.

Holder threads come from a process-wide pool: a thread runs one handle at a time,
then waits up to one second for the next one (at most two wait; busy holders are
never limited). A thread that left any ownership state behind exits instead of
waiting. Each connection keeps the closed private `SharedEngine` wrapper of its
last handle for the next one: only settings, mutex objects and owner bookkeeping
survive, never a core, page cache, WAL index, mapped coordination participation or
the mutex. A wrapper is discarded after any failure, when the connection's password
or collation changed (a rebuild), and when the connection is disposed or collected.

A Shared handle dropped without completion while its `LiteDatabase` stays alive
therefore blocks every other writer in every process until the database is
disposed. A dropped handle is released by finalization only when its database is
abandoned too.

**While a Shared handle is open, every ordinary or legacy call to the same database
that needs its writer mutex waits until the handle completes** — all writes, and reads
other than those served by a qualified mapped snapshot on .NET 8+, through any
connection. Once a handle's transaction exceeds its page limit and spills
uncommitted pages to the WAL, mapped reads fall back to the mutex too until it
completes, as with a spilled legacy transaction. Unlike a legacy `BeginTrans` block,
whose same-thread calls join it, such a call from the code that will later complete
the handle (for example after an `await`) never returns, unless another thread
completes the handle meanwhile. Obtain everything the flow needs from the handle, or
complete the handle first. Opt in to `SharedSelfWaitGrace` to refuse such a wait,
from the async flow that began or used the open handle, with
`InvalidOperationException` (immediately, or once the handle stayed idle for the grace
period), and `SharedWriterTimeout` to bound every Shared writer wait; see
[Shared writer waits](shared-writer-waits.md). Direct mode has no such wait outside
collection locks and exclusive maintenance.

At most one handle per database (mutex name) in a process proceeds to native
admission; later begins wait on their own threads, never on an extra holder. As with
existing Shared writers, `BeginTransaction()` waits until the current owner releases,
without a timeout unless `SharedWriterTimeout` is set; do not begin a second handle on
the only thread able to complete the first. A handle belongs to its `LiteDatabase`:
disposing a caller-owned `SharedEngine` underneath (`disposeOnClose: false`) does not
end it; dispose the handles or the database.

Operations that would wait for writer ownership that only the executing handle can
release are refused with `InvalidOperationException` before waiting: an ordinary call
on the same database (through any connection) from a handle callback that needs the
writer mutex (a read served by a mapped snapshot proceeds), or a begin from
inside an ordinary call or reader retaining the mutex on that thread. The begin-side
refusal covers this connection's retained ownership and executing calls of any
connection. A begin from a thread that retains ownership through *another*
connection to the same file (an open legacy transaction or locking reader of that
connection) waits, as an ordinary write in that situation does; dispose that
connection from another thread to let it proceed
([#3073](https://github.com/litedb-org/LiteDB/issues/3073)). Work on other
databases remains independent. The holder does not retain the caller's execution
context, settings subclass or collation object, so a handle abandoned together with
its database releases writer ownership after finalization.

## Migrating legacy callers

`BeginTrans`, database-level `Commit` and `Rollback` keep their signatures and
thread-bound behavior, and now emit **CS0618** (`Obsolete`, warning only). Replace
the trio with a handle and obtain the participating collections from it. Crossing
`await` remains unsafe for the legacy API.

One difference to plan for: any failure of a handle call ends the handle (`Failed`,
rolled back), including a client-side argument check that fails before reaching the
engine, such as `FindById(null)`, which leaves a legacy transaction untouched.
Validate inputs before the call, or begin a new handle after a failure.

Projects using `TreatWarningsAsErrors` can migrate incrementally with
`<WarningsNotAsErrors>$(WarningsNotAsErrors);CS0618</WarningsNotAsErrors>`, or a narrow
`#pragma warning disable CS0618` around intentional legacy calls.

## Not included

These parts of the v6 plan are separate follow-ups: the per-call
`BeginTransaction(TimeSpan, CancellationToken)` admission controls (the connection-wide
`SharedWriterTimeout` bounds begin), bounded session
close with deferred cleanup ([#3067](https://github.com/litedb-org/LiteDB/issues/3067)),
a pooled Direct host shared by facades of one file
([#3041](https://github.com/litedb-org/LiteDB/issues/3041)), keeping a Shared handle's
storage core or writer mutex between handles, and transaction-bound SQL and FileStorage.
