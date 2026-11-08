# Synchronous transaction handles

`LiteDatabase.BeginTransaction()` returns an `ILiteTransaction` with independent
ownership. Use `transaction.GetCollection<T>()` (or the BSON overload) for every
participating operation. Ordinary database collections and SQL keep their own
transactions, including calls from mapping/input callbacks.

```csharp
using (var transaction = database.BeginTransaction())
{
    transaction.GetCollection("items").Insert(new BsonDocument { ["_id"] = 1 });
    transaction.Commit();
}
```

Direct files, memory, temporary storage, caller streams and filename Shared databases support sequential
handoff to another thread, including after the creator exits. One handle cannot
execute overlapping or reentrant operations. Close bound readers before commit;
that refusal leaves the handle active. Dispose rolls back an active handle.
Completed handles and their collections, queries and deferred results reject reuse.
Statement failures end the handle; they cannot silently start an automatic transaction.
Outcome remains observable after disposal, including Failed and Indeterminate.
Committed reflects the configured durability contract (`DurableCommits=false`
retains its documented weaker guarantee).

Typed/BSON CRUD, indexes, vector queries, Include, deferred queries/readers and
collection metadata are bound. SQL, file storage, drop/rename, maintenance and
pragma mutation are outside the handle surface. Only `$cols` and `$indexes`
metadata inputs are supported among system collections; external input/output is
refused before side effects. Read-only handles support reads and completion.

`ILiteDatabase` and `ILiteEngine` retain their member sets. The extension on
`ILiteDatabase` requires the optional `ILiteTransactionProvider`. Custom engines
and decorators are refused before admission; the database never unwraps a
third-party decorator. Shared uses the existing holder, participant and retained core. Every handle reserves
writer ownership for its whole lifetime, including a read-only handle. This can
block writers in other processes until commit, rollback or disposal. The native
mutex stays on its holder thread; sequential handle operations can change threads
without releasing the transaction hold. Creator-thread exit does not abandon it. Shared handles refuse private storage,
caller streams and Windows thread impersonation before admission. An identity
refusal leaves an existing handle active for use after reverting impersonation.

Legacy `BeginTrans`, `Commit` and `Rollback` remain thread-bound and issue CS0618
warnings on both client declarations. Migrate to completion on the returned
handle or narrowly suppress CS0618 for deliberately synchronous legacy code.
A same-thread legacy owner blocks a new handle with error 141, including a peer
Direct facade. Independent Direct handles begin immediately; competing collection
writes wait under the existing TIMEOUT and fail with error 120. Each synchronous
handle call samples TIMEOUT once and shares its remaining allowance across
admission and collection waits. Only blocking wait time consumes the allowance;
application callbacks and commit durability are not execution time limits.
Collection waiters advance in FIFO order, and timeout removes only that waiter.
An idle handle can complete on another thread while its creator waits; the creator
thread alone does not cause error 141. Conflicting work inside an executing
handle callback is refused with InvalidOperationException before blocking.
Direct retains native admission for the host lifetime. Shared applies the same
sampled wait allowance to local admission, mapped requests, pin and native waits.
An open handle is one tenure unit: pressure never preempts its transaction or
counts statements as separate completions. The existing 8-completion / 1 ms
pressure budget is checked when the handle releases its final hold, before a
successor transaction can reuse a tenure due to yield. A waiter can time out while
an idle handle stays active; callers must arrange completion on another thread.
Shared samples the last safely observed TIMEOUT. Mapped startup captures it during
protected validation (whose separate 5-second bound remains unchanged). A legacy
cold facade starts with 60 seconds, then clamps to a smaller safely learned timeout;
a remote pragma change applies only after a safe refresh. There is no
async API or ambient enlistment.
