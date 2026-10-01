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
ending there) succeeds, and the reader is released when that call returns, so
it never keeps commit refused.

An ordinary call that needs a collection lock or exclusive maintenance held up
by an *idle* handle waits for the existing lock timeout, like any other conflicting
transaction; when the same flow must complete that handle, it then fails.

Raw `LiteEngine` calls from a bound callback are rejected before side effects. An
ordinary callback write that needs a collection lock held by the executing handle,
or a rebuild that needs exclusive admission, fails immediately with the existing
lock-timeout error instead of waiting for work that only its own return can release.

## Supported surface

| Backend or API | Handle support |
| --- | --- |
| Direct (file, memory, temporary, caller streams) | Yes |
| Shared, filename-backed | Yes; see below |
| Read-only Direct/Shared | Queries and completion; writes are refused and leave the handle active |
| Shared memory/temporary storage or caller-supplied streams | `NotSupportedException` before admission |
| Shared begin under Windows thread impersonation | `NotSupportedException` before admission |
| Coordinated, custom or decorated engines | `NotSupportedException`; legacy/ordinary use unchanged |
| Typed/BSON collections, bulk input, queries, Include, vector queries | Yes |
| Index creation/removal, `GetCollectionNames`, `CollectionExists`, `$cols`/`$indexes` | Yes |
| Collection drop/rename | `NotSupportedException` before mutation |
| SQL, FileStorage, nested begin, checkpoint, rebuild, pragma changes | Not exposed through a handle |
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
committed outcome stays `Committed` even if later cleanup fails. Commit that finds
its transaction already ended by an engine stop or close throws (the engine's
published failure where there is one) and reports `Failed`; it never returns as if
it committed. Disposing a healthy
active handle rolls back; if another failure already stopped the engine, disposal
releases the handle without rethrowing that failure.

Completion releases the transaction's locks, Shared writer ownership and storage
references, and unregisters the handle, whether or not it is disposed.

Disposing the database refuses new handles, waits for a handle call executing on
another thread to return, then rolls back every handle that is still active before
releasing the engine, including with `disposeOnClose: false`. Disposing the database
from inside an executing handle call throws `InvalidOperationException` before any
state changes. The wait is not bounded; a bounded close with deferred cleanup is the
session-lifetime contract of [#3067](https://github.com/litedb-org/LiteDB/issues/3067).

## Shared mode

A Shared handle owns the native writer mutex for its whole lifetime. A dedicated
holder thread acquires it and opens a fresh storage core for the handle; the
application's threads run the handle's operations on that core in turn. Commit,
rollback or disposal closes the core and releases the mutex on the holder thread,
so other processes and connections then proceed. Completed handles retain neither.

**While a Shared handle is open, every ordinary or legacy call to the same database
that needs its writer mutex waits until the handle completes** — all writes, and reads
other than those served by a qualified mapped snapshot on .NET 8+, through any
connection. Unlike a legacy `BeginTrans` block, whose same-thread calls join it, such a
call made by the code that will later complete the handle (for example after an
`await`) never returns. Obtain everything the flow needs from the handle, or complete
the handle first. Direct mode has no such wait outside collection locks and exclusive maintenance.

At most one handle per database (mutex name) in a process proceeds to native
admission; later begins wait on their own threads, never on an extra holder. As with
existing Shared writers, `BeginTransaction()` waits until the current owner releases;
do not begin a second handle on the only thread able to complete the first.

Operations that would wait for writer ownership that only the executing handle can
release are refused with `InvalidOperationException` before waiting: an ordinary call
on the same database (through any connection) from a handle callback, or a begin from
inside an ordinary call or reader retaining the mutex on that thread. Work on other
databases remains independent. The holder does not retain the caller's execution
context, settings subclass or collation object, so a handle abandoned together with
its database releases writer ownership after finalization.

## Migrating legacy callers

`BeginTrans`, database-level `Commit` and `Rollback` keep their signatures and
thread-bound behavior, and now emit **CS0618** (`Obsolete`, warning only). Replace
the trio with a handle and obtain the participating collections from it. Crossing
`await` remains unsafe for the legacy API.

Projects using `TreatWarningsAsErrors` can migrate incrementally with
`<WarningsNotAsErrors>$(WarningsNotAsErrors);CS0618</WarningsNotAsErrors>`, or a narrow
`#pragma warning disable CS0618` around intentional legacy calls.

## Not included

These parts of the v6 plan are separate follow-ups: the opt-in
`BeginTransaction(TimeSpan, CancellationToken)` admission controls, bounded session
close with deferred cleanup ([#3067](https://github.com/litedb-org/LiteDB/issues/3067)),
a pooled Direct host shared by facades of one file
([#3041](https://github.com/litedb-org/LiteDB/issues/3041)), Shared holder reuse, and
transaction-bound SQL and FileStorage.
