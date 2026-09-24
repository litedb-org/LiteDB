# Shared-mode storage guarantees

Shared connections use the same durable commit, data-page checksum, WAL
verification, index ordering, compact storage and MVCC implementations as direct
connections. They add a named mutex for writer/recovery admission and file-handle
leases for streaming snapshots. This protocol is intended for processes on one
Windows, Linux or macOS host with working named mutexes and file-sharing locks.

```csharp
using var db = new LiteDatabase(new ConnectionString
{
    Filename = "application.db",
    Connection = ConnectionType.Shared,
    DurableCommits = true
});
```

`DurableCommits` defaults to true. Checksums and MVCC require no separate shared
mode switch. See [checksums](page-and-wal-checksums.md),
[MVCC coordination](mvcc-checkpoint.md#shared-mode), and the
[storage acceptance map](storage-stack-safety.md).

## Connection identity and lifetime

A shared connection captures its absolute database filename at construction.
Changing the process working directory later does not redirect a reopen, its
WAL/temp files, mutex or reader registry to another database. The caller's
`EngineSettings` object is cloned and remains unchanged.

All participating processes must use the same database path, mutex naming
strategy and coordination protocol. Absolute path normalization does not resolve
symbolic links, hard links or other physical-file aliases; different aliases
must not be used to open the same database concurrently. Direct connections and
older shared implementations must not run concurrently with shared snapshots.

Ordinary small queries finish under the mutex. Larger queries retain a snapshot
and an OS-owned lease, releasing the writer mutex before returning. A paused
reader remains live; a terminated process loses its handles. Explicit
transactions, `FOR UPDATE` and `SELECT INTO` retain writer serialization. Rebuild
requires shared readers to close. Preserve the `-readers` directory while the
database is in use; it is part of coordination, not a disposable cache.

A connection's mutex ownership does not belong to the thread that acquired it:
a holder thread takes and releases the OS mutex, and one thread of the
connection owns it recursively. A result streamed under the mutex (no lease
could be registered) and the connection itself may be disposed on any thread,
for example after an `await`, without an `ApplicationException` or a leaked
mutex. When the owning thread exits with a result or transaction open, the
connection drops its engine without writing and releases the mutex, as the OS
abandons a mutex whose thread exits; other threads and processes neither wait
nor meet the dropped engine's file handles. An exited transaction owner is
reported to the connection's next call. Explicit transactions must still be
completed on the thread that began them. An operation's release is posted to the
holder. A connection's own checkpoint attempts wait for that release before trying
the mutex, and `Dispose` returns only after it: a disposed connection holds no
mutex, so the next connection's final close finds it free.

Each operation opens and closes an engine. Its close checkpoints only once the
WAL holds 50 pages (or the CHECKPOINT pragma if smaller), so between operations
committed work can remain in the WAL, which stays authoritative: a killed process
leaves a WAL that the next open recovers. Disposing the connection checkpoints the
remainder when the mutex is free; otherwise the current owner, a live connection,
checkpoints at its own close. Once every connection has closed, the data file
alone is the database again, unless a reader of another connection still pins the
WAL or CHECKPOINT is 0.

After an abandoned explicit transaction, the connection discards that engine
without its close checkpoint: another process may have committed or checkpointed
meanwhile, so the engine's WAL index and cache can be stale. The next open
recovers the WAL as usual.

## Durability reporting

An ordinary commit whose device sync is explicitly unsupported follows the
stack's existing compatibility fallback to an OS-cache flush. Other sync errors
fail the commit; the outcome may be unknown and recovery must choose a complete
transaction. Protected checkpoint/header/retirement barriers always attempt a
real device sync. Only storage that answers "cannot sync" (#2242) falls back to
ordered OS-cache flushing there, which survives a process crash but not power
loss; any other sync error stops the barrier before data is overwritten. On such
storage reclaimed WAL slots are never reused. Every shared operation opens a fresh
engine, so before its first reuse of a slot found blank at open, that engine syncs
the raw log once; a "cannot sync" answer makes it append instead.

`$database.durableLogFlush` is false when the connection opts out of device sync
or has acknowledged a commit after that fallback. Shared connections retain this
diagnostic across internal engine reopenings, including diagnostic queries. A
later engine still attempts device sync; retaining the diagnostic does not disable
sync. A new independent connection starts with its own diagnostic state. The
value describes that connection, not every writer that has accessed the file.

## Safety coverage

| Invariant | Discriminating coverage |
| --- | --- |
| A relative connection stays bound to its original database and registry after a working-directory change, before or after its first operation. | [SharedSafetyProcess_Tests](../LiteDB.Tests/Internals/SharedSafetyProcess_Tests.cs): separate child process, plain/encrypted files, a retained snapshot, and an unrelated same-named database whose records must remain unchanged. |
| A result or connection disposed on another thread, or an owner thread that exits, never keeps other threads or processes waiting, and an exited transaction owner is still reported. | [SharedMutexOwnership_Tests](../LiteDB.Tests/Engine/SharedMutexOwnership_Tests.cs): unleased readers disposed on other threads and after an await, `Dispose` on another thread, exited reader and transaction owners, and a real second process that waits only while the reader is open. All six fail with the thread-affine mutex. |
| An abandoned explicit transaction's engine is discarded without a checkpoint, and another connection's commits made meanwhile survive. | [Issue3005_AbandonedOrphan_Tests](../LiteDB.Tests/Issues/Issue3005_AbandonedOrphan_Tests.cs): checkpoint stages observed during the discard (none allowed), plain/encrypted, with and without a reader lease held by the other connection; the concurrent-commit variant runs on Unix hosts, where a peer can write while the orphan's handles stay open. |
| Storage that cannot sync never has reclaimed WAL slots reused by later shared engines; a live reader keeps its snapshot and a crash image recovers. | [SharedUnsyncableLog_Tests](../LiteDB.Tests/Internals/SharedUnsyncableLog_Tests.cs): a leased reader across a snapshot checkpoint and later writes on fresh engines; fails with 315 overwritten slots when the reuse probe is removed. |
| An operation's close checkpoints only a WAL past its threshold; the connection's final close, and the last streamed result's disposal, checkpoint the rest; read-only connections change neither file; the WAL between operations recovers every committed operation. | [SharedLazyCheckpoint_Tests](../LiteDB.Tests/Engine/SharedLazyCheckpoint_Tests.cs): counts reclaiming checkpoints below and past the threshold, a smaller or disabled CHECKPOINT pragma, explicit checkpoint, two connections closing in either order, byte-preserving read-only access and plain/encrypted crash images. Both the old close-every-operation behavior and a missing final checkpoint fail it. |
| Confirmation respects the durability setting, and fallback cannot be hidden by the next shared operation. | [SharedDurability_Tests](../LiteDB.Tests/Internals/SharedDurability_Tests.cs): observed device syncs, automatic/explicit commits, encrypted wrappers, injected unsupported sync, retry and connection-local diagnostics. |
| Lost/torn new writes cannot damage the previously acknowledged prefix or expose a partial transaction. | [SharedCommitFailure_Tests](../LiteDB.Tests/Internals/SharedCommitFailure_Tests.cs): durable/volatile file images, lost writes, later sectors persisting ahead of a torn frame, failure after successful sync, and a successful-sync control. Covers BSON/compact, plain/encrypted, automatic/explicit commits; repeated shared recovery checks full documents, indexes and an untouched collection. A foreign thread verifies writer-mutex release. |
| Process death preserves acknowledged transactions, discards unconfirmed safepoints and preserves another process's snapshot. | [SharedStorageProcess_Tests](../LiteDB.Tests/Internals/SharedStorageProcess_Tests.cs): actual killed writers, proven nonempty/unconfirmed WAL, full payload/index checks and checkpoint after readers drain. |
| Damaged data fails with a page diagnostic; damaged WAL follows the verified-prefix recovery policy and reports discarded bytes. | The same process suite checks repeated data-page rejection, read-only data/WAL byte preservation, repeated recovery and checkpoint/reopen across BSON/compact and plain/encrypted files. |

Run the focused suite with `TestingEnabled=true` and `tests.runsettings`:

```sh
dotnet test LiteDB.Tests -c Release -f net8.0 -p:TestingEnabled=true --settings tests.runsettings --filter 'FullyQualifiedName~SharedLazyCheckpoint|FullyQualifiedName~SharedSafetyProcess|FullyQualifiedName~SharedMutexOwnership|FullyQualifiedName~Issue3005|FullyQualifiedName~SharedUnsyncableLog|FullyQualifiedName~SharedStorageProcess|FullyQualifiedName~SharedDurability|FullyQualifiedName~SharedCommitFailure'
```

Repeat on `net10.0`; the stream-fault and durability tests also compile/run on the
Framework targets. Process tests use the packaged .NET harness, launched by the
test host's runtime installation with an explicit runtime version; the child
checks that its actual runtime and architecture match the parent. The existing
platform CI runs them on Windows, Linux and macOS; a local Linux run does not
establish the results for the other operating systems.

The fault model assumes successful device sync persists addressed bytes and
power-safe overwrite preserves synced bytes outside the write range. Process
termination alone does not simulate loss of the OS cache. Checksums detect
accidental damage and do not reconstruct data without intact recovery evidence.
No on-disk format, upgrade, checkpoint or retirement protocol changes are needed
for these shared-connection fixes.
