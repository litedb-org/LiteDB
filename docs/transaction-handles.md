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

Direct files, memory, temporary storage and caller streams support sequential
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
third-party decorator. Shared `BeginTransaction()` currently throws
`NotSupportedException`; Shared integration is phase 4 step 5.

Legacy `BeginTrans`, `Commit` and `Rollback` remain thread-bound and issue CS0618
warnings on both client declarations. Migrate to completion on the returned
handle or narrowly suppress CS0618 for deliberately synchronous legacy code.
A same-thread legacy owner blocks a new handle with error 141, including a peer
Direct facade. Independent Direct handles begin immediately; competing collection
writes wait under the existing TIMEOUT and fail with error 120. The unified
multi-stage deadline and waiting protocol remains phase 4 step 4. There is no
async API or ambient enlistment.
