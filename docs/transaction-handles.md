# Synchronous transaction handles

Use `BeginTransaction()` on a `LiteDatabase` or an `ILiteDatabase` whose provider
implements `ILiteTransactionProvider`. It returns an independent `ILiteTransaction`.
Existing `ILiteDatabase` implementations and engine decorators do not acquire this
capability automatically; unsupported providers throw `NotSupportedException`
before starting a transaction. Legacy `BeginTrans`, database `Commit` and database
`Rollback` remain callable, with CS0618 warnings on the database API.

Get collections and queries from the handle. Ordinary database collections never
enlist in it. Use one handle sequentially: another `Thread` can take over after the
previous call returns, including completion after the creator thread exits.
Overlapping and callback reentrant calls are rejected. Close bound readers before
commit; bound objects cannot be reused after completion or disposal.

The following complete program is extracted, compiled with warnings as errors and
executed by the transaction-handle compatibility workflow on .NET 8 and 10.

```csharp
using System;
using System.IO;
using System.Threading;
using LiteDB;

var directory = Path.Combine(Path.GetTempPath(), "litedb-handle-doc-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    using var db = new LiteDatabase(Path.Combine(directory, "direct.db"));
    db.Timeout = TimeSpan.FromSeconds(1);
    using (var transaction = db.BeginTransaction())
    {
        var rows = transaction.GetCollection("rows");
        rows.Insert(new BsonDocument { ["_id"] = 1, ["value"] = "committed" });
        Exception failure = null;
        // Join establishes sequential ownership transfer and transports worker failure.
        var worker = new Thread(() =>
        {
            try { transaction.Commit(); }
            catch (Exception error) { failure = error; }
        });
        worker.Start();
        worker.Join();
        if (failure != null) throw failure;
    }
#pragma warning disable CS0618 // Narrow bridge for the legacy migration example.
    db.BeginTrans();
    using (var sameFile = new LiteDatabase(Path.Combine(directory, "direct.db")))
    {
        try
        {
            using var forbidden = sameFile.BeginTransaction();
            throw new Exception("Expected 141 behind this thread's legacy owner");
        }
        catch (LiteException error) when (error.ErrorCode == 141) { }
    }
    db.Rollback();
#pragma warning restore CS0618
    using (var abandonedWork = db.BeginTransaction())
        abandonedWork.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 2 });
    if (db.GetCollection("rows").Count() != 1) throw new Exception("Dispose must roll back");

    var connection = $"Filename={Path.Combine(directory, "shared.db")};Connection=Shared";
    using var shared = new LiteDatabase(connection);
    using var peer = new LiteDatabase(connection);
    peer.Timeout = TimeSpan.FromSeconds(1);
    using (var reader = shared.BeginTransaction())
    {
        _ = reader.GetCollection("rows").Count();
        // Even a read-only Shared handle reserves the writer until completion.
        try
        {
            using var blocked = peer.BeginTransaction();
            throw new Exception("Expected TIMEOUT behind the held writer");
        }
        catch (LiteException error) when (error.ErrorCode == 120) { }
        reader.Rollback();
    }
    using (var successor = peer.BeginTransaction()) successor.Commit();
    Console.WriteLine("PASS documentation examples");
}
finally
{
    Directory.Delete(directory, true);
}
```

`TIMEOUT` is the database timeout pragma (`Timeout` in C#). Each synchronous call samples one
budget across local admission, native ownership and later nested lock waits;
user work does not consume wait time. An idle handle may be completed elsewhere
while another caller waits. Two handles on the same thread follow this waiting
rule and can receive error 120. Do not wait for yourself when no other thread can
complete the holder. No async API or ambient transaction flow is provided.

Legacy transactions remain bound to their thread. Error 141
(`LiteException.SAME_THREAD_TRANSACTION`) refuses a conflicting same-thread
legacy-owner operation immediately. In particular, if facade A owns a legacy
transaction, `B.BeginTransaction()` for the same database on that thread throws
141 (Q1), rather than waiting. Finish the legacy transaction on its owning thread
before using that peer. Executing callbacks cannot wait for ownership on which
they themselves depend.

Shared handles reserve writer ownership for their entire active lifetime, even
when they only read (Q2). Thread handoff retains the reservation. Other writers
wait according to TIMEOUT; retained process-host or mapped-reader infrastructure
does not make an active handle preemptible. Keep transactions short and dispose
them deterministically.

Disposing an active handle rolls back. Commit/rollback set the outcome; disposal
does not change a known committed outcome into rollback. An uncertain commit can
report `Indeterminate`; establish the outcome by reading after recovery before
retrying business operations. Dropping a reference or ending the creator thread
does not release a handle retained by a live database. Closing the database
refuses new begins, cancels pending acquisition, rolls back idle handles and
drains executing calls/readers. Closing from its own executing callback is
refused. An unreachable database/handle graph can be cleaned by a reaper, with no
GC timing guarantee. Explicitly dispose caller-owned streams and databases as
required by their ownership contracts. See [lifetime details](transaction-handles/lifetime.md).

With durable commits enabled, a returned `Commit()` acknowledges the entire transaction:
recovery must retain its documents and indexes. A process can die after the WAL
confirmation becomes durable but before `Commit()` returns. Recovery may then
retain that complete transaction even though the application received no acknowledgement.
An in-flight commit must recover atomically, with every index consistent; it cannot
be classified as rolled back merely because the caller did not observe success.
`LiteTransactionState.Indeterminate` represents this uncertainty when a surviving
caller observes a commit failure after persistence may have occurred. A dead
process cannot report a state; resolve its operation by querying after recovery
before retrying, using an application operation identifier to avoid duplication.

Unconfirmed work, including spilled pages from a handle that never starts commit,
must be absent after recovery. For process death, a written WAL confirmation can
survive in the OS cache even before the durable-flush call: cuts after confirmation
but before a completed barrier permit either complete presence or complete absence.
Cuts before confirmation require absence; cuts after a successful covering durable
flush require presence, even before application acknowledgement. Process death
keeps OS caches and is distinct from modeled power loss. In Shared mode, recovery
must also release the dead handle's tenure so an already-live peer can acquire a
handle, write, and commit. See the maintained [commit-cut matrix](../LiteDB.Tests/Internals/TransactionHandleReviewSafety_Tests.cs).

To migrate from `BeginTrans()`, replace the boolean begin with a `using` handle,
get every transactional collection from that handle, and call its `Commit()` or
`Rollback()`. Dispose without commit to roll back. Do not call database `Commit()`
to complete a handle. Existing code can temporarily use
`<WarningsNotAsErrors>CS0618</WarningsNotAsErrors>` when treating other warnings as
errors. Custom providers must explicitly implement the optional capability;
transparent legacy decorators continue to use the legacy transaction API.

The [source inventory disposition](transaction-handles/source-inventory.md)
records the source-port scope and remaining evidence boundaries.

Supported storage and operation boundaries remain explicit: Direct files, memory,
temporary storage and caller streams support handles; filename Shared handles
refuse private storage and caller streams before admission. Windows thread
impersonation is refused before Shared admission or use, leaving an existing
handle usable after reverting identity. Typed/BSON CRUD, indexes, vector queries,
Include, deferred readers and collection metadata are bound. SQL, file storage,
drop/rename, maintenance and pragma mutation remain outside the handle surface.
Only `$cols` and `$indexes` are supported system collection inputs. Statement
errors terminate the handle; they cannot fall back to an automatic transaction.
Commit reflects the configured durability contract, including the weaker
`DurableCommits=false` guarantee.

Direct retains native admission for the process-host lifetime. Shared retains the
existing holder, participant and core; its 8-completion / 1 ms pressure budget is
checked after the final logical hold releases. Pressure never preempts an active
handle. Shared samples the last safely observed TIMEOUT. Mapped startup learns it
under protected validation, whose separate 5-second bound is unchanged. A legacy
cold facade starts with 60 seconds, then clamps to a smaller safely learned
value; remote pragma changes require safe refresh. Application callbacks and
commit durability are not bounded execution time. Collection waiter timeout
removes only that FIFO waiter.

For custom engines compiled against v5.0.21, see the [known pre-existing vector
interface ABI break](release-notes.md#known-pre-existing-custom-engine-abi-break).
They require recompilation; this predates handles. The compatibility package
separately verifies unchanged released client callers and current-dev decorators.
