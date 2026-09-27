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

A pure read (no transaction, `FOR UPDATE` or `SELECT INTO`) runs on a read-only
snapshot engine opened under the mutex; it never writes, never deletes the WAL
and uses private sort space. A result of at most 100 values and 64 KiB finishes
under the mutex. A larger one registers its lease for that engine's read version
while the mutex is still held and then continues the same reader, so the query
runs once. A file that needs a writable open first (creation, upgrade, index
migration, promotion, auto-rebuild) is read through the writable engine as
before. Leases are exclusive handles created with delete-on-close: a held lease
cannot be taken by a prober's exclusive open, and a closed one removes itself.
Registration does not scan the registry; checkpoints remove the leases of
crashed readers and fail closed on a registry they cannot read (performance
background: [shared read performance](shared-read-performance.md)). A paused
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

On Windows a connection keeps its data and WAL file handles open between
operations; opening them was most of an operation's fixed cost. Only the handles
are reused. Every operation still reads the header, WAL and pages afresh, because
another process may have committed, checkpointed or reused WAL slots meanwhile.
Before reuse, a handle must still name the file at its original path: a deleted
(for example the WAL after another connection's final checkpoint), replaced
(rebuild) or renamed file is closed and the path opened again. Each open stream
owns one handle, whose OS file pointer is not shared. Shared connections open the
files with read, write and delete sharing, so they coexist with each other and a
checkpoint can delete the WAL. While any shared connection is open, and not only
during an operation, direct connections and other openers that deny write sharing
(`FileShare.Read`) are refused; `File.Copy` shares write access and still works.
Other platforms open the files for every operation as before, and so do Windows
volumes that do not report POSIX delete semantics (`FILE_SUPPORTS_POSIX_UNLINK_RENAME`;
for example FAT, exFAT and many network or third-party file systems). There a
deleted WAL keeps its name pending until every handle closes, so a peer's idle
cached handle would make the next WAL creation fail with access denied.

After an abandoned explicit transaction, the connection discards that engine
without its close checkpoint: another process may have committed or checkpointed
meanwhile, so the engine's WAL index and cache can be stale. The next open
recovers the WAL as usual.

## Diagnostics and mode admission

Retain the `SharedEngine` passed to `LiteDatabase` and call `GetDiagnostics()` to
observe the connection without opening storage. `ReadPath` uses the
`SharedReadPath` enum: `Uninitialized`,
`Protected`, `Mapped`, `Revoked`, or `Disposed`. `Mapped` means a usable authority
is attached; an individual query can still need the protected path, so consult
`CoordinatedReadHits` and `CoordinatedReadMisses` as well. Misses count eligible
cached-query attempts, including warmup. The snapshot also exposes the last
fallback reason (exception types and messages, without stack traces), active
snapshot leases, writer-pressure requests and reader
yields. Counts are cumulative and observations are not transactionally consistent.
`ProcessMappedParticipants` counts attachments to this path in this process;
it is **not** the number of participating OS processes. No diagnostic counter
is stored in the mapped correctness protocol. On older targets the path is
protected and the reason identifies the runtime restriction.

The `LiteDB-Shared` EventSource emits event 1 with `filename`, `state`, and
`reason`. States include `attached`, `detached`, `fallback`, `revoked`, `created`,
`retired`, and `mode-conflict`. Creation/retirement events identify the control
file path, including a newly recreated authority. These are opt-in lifecycle
events, not one event per read. Listener failures cannot authorize access or
interrupt storage cleanup.

Modern writable file engines and mapped participants hold an OS file-sharing
lease on `<database>-shared-mode`. Direct writers require exclusive admission;
Shared participants share a lease bound to the effective mutex name. Different
mutex strategies that resolve to the same name remain compatible. Detectable
conflicts and guard access failures throw the public `SharedModeConflictException`
(an `IOException`, retaining the underlying cause in `InnerException`) before
opening writable storage, including
upgrade/recovery, and Direct rebuild keeps its admission through replacement.
Mapped streaming snapshots retain admission after their connection is disposed.
Process termination releases the OS lease automatically. An incomplete idle
identity can be rewritten only after obtaining exclusive admission. An unknown
identity is preserved and refuses Shared writes instead of truncating an unrelated
file that occupies the control path.

The guard file remains after close and must not be removed or replaced while
connections are open. It contains no database data, requires no durable database
migration, and can be recreated when the database is offline. Failed writes or
process death while initializing it cannot change data or WAL bytes. Read-only
Shared connections never create or rewrite a mode identity. If the identity is
absent or idle but mismatched, repeated reads remain protected; mapped attachment
requires an existing matching lease and keeps it until disposal. Protected read-only
fallback can run without holding a mode guard. Direct read-only connections also
bypass admission. Thus read-only mixing is not universally rejected: only mapped
participants have the additional lifetime protection against Direct writers.
`ReadOnly` combined with `Upgrade` or `AutoRebuild` can write during open and must
acquire writable admission. Private rebuild/upgrade candidates do not create guard
files; the live engine retains admission across publication.
Memory databases, caller data streams, and names outside the supported control
path limits retain their existing behavior.

An already active pre-guard mapped participant is detected by its held
`-shared-live` handle when a modern Direct writer opens. Unreadable participation
authorities and orphan status pages also block Direct writers; protected Shared
fallback and read-only inspection remain available. Unknown coordination
ABIs still follow the existing rejection/revocation protocol. Old executables
that never check the guard, or start participating after a modern writer's
checks, cannot be made safe by this library alone. Concurrent use with those
executables remains unsupported. Physical-file aliases and external deletion of
coordination files also remain outside the protocol: always use the same path.
The guard depends on working OS file-sharing locks, as do snapshot leases.

### Compatibility impact for Direct connections

Ordinary writable Direct connections also require the persistent `-shared-mode`
file, even for databases that have never used Shared. Skipping admission until a
Shared artifact appears would race a Shared participant arriving after Direct
opens. Guard creation/open failures stop access before data/WAL mutation; there
is no best-effort bypass. The directory must permit creating the sidecar, or an
existing sidecar must be writable. Disabling .NET file-sharing locks on Unix now
rejects writable Direct too, with an error naming Direct admission. NFS/SMB
workarounds that disable locks do not satisfy this contract.

Offline backup/copy tools can omit the guard because it contains no database
state. Deleting the database file alone leaves it behind; delete it only after
all users of that database path have closed. Do not remove a live guard to work
around an admission error. See the [release notes](release-notes.md).

[SharedModeDiagnostics_Tests](../LiteDB.Tests/Engine/SharedModeDiagnostics_Tests.cs)
checks the public observations, lifecycle events, conflicting writers in real
processes, plain/encrypted byte preservation and indexed cold reopens, Direct
process death, failed opens, rebuild and protected read-only fallback.
[SharedModeGuardFailure_Tests](../LiteDB.Tests/Engine/SharedModeGuardFailure_Tests.cs)
checks process termination during initialization, injected initialization I/O
failure, older held participation handles, readers outliving their connection,
and admission during successful and failed rebuilds.
[SharedModeAdmissionReview_Tests](../LiteDB.Tests/Engine/SharedModeAdmissionReview_Tests.cs)
checks absent/mismatched read-only identities, retained mapped admission,
unavailable Direct guards, disabled locking, and sidecar-free rebuild candidates.
[Issue2965_Tests](../LiteDB.Tests/Issues/Issue2965_Tests.cs) forces rebuild to
finish before transaction admission release returns; owner-local slot and cache
cleanup must finish before that release, while those services are still alive.
The fault model is process death and failed control-file initialization with OS
locks intact, not malicious unlinking or unreliable network locking. No data/WAL
publication, checkpoint, or power-loss recovery protocol is changed.

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
| A pure read executes once on one snapshot: a result at the 100-value / 64 KiB budget completes under the mutex, one past it streams from the same snapshot under a lease registered before the mutex is released. Files that need a writable open (missing, legacy, invalid state with auto-rebuild) are still read through it. A held lease is live for other registries and a closed one leaves no file. Auto-rebuild never replaces files under a live reader. | [SharedReadPath_Tests](../LiteDB.Tests/Engine/SharedReadPath_Tests.cs) |
| An abandoned explicit transaction's engine is discarded without a checkpoint, and another connection's commits made meanwhile survive. | [Issue3005_AbandonedOrphan_Tests](../LiteDB.Tests/Issues/Issue3005_AbandonedOrphan_Tests.cs): checkpoint stages observed during the discard (none allowed), plain/encrypted, with and without a reader lease held by the other connection; the concurrent-commit variant runs on Unix hosts, where a peer can write while the orphan's handles stay open. |
| Cached handles are reused only while they name the file at their path: commits, checkpoints that delete the WAL and rebuilds by other connections or processes stay visible, no commit reaches a deleted WAL, and a killed process holding handles blocks no peer. | [SharedFileHandles_Tests](../LiteDB.Tests/Internals/SharedFileHandles_Tests.cs): cached reads compared with a fresh connection, plain and encrypted, real child processes; without the identity check four of seven fail: three lose an acknowledged commit to a deleted WAL, one reads the replaced data file. Direct mode and write-denying readers are refused while a shared connection is open; `File.Copy` works. |
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
