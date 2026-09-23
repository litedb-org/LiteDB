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

## Durability reporting

An ordinary commit whose device sync is explicitly unsupported follows the
stack's existing compatibility fallback to an OS-cache flush. Other sync errors
fail the commit; the outcome may be unknown and recovery must choose a complete
transaction. Protected checkpoint/header/retirement barriers still require
successful device sync and never use this fallback.

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
| Confirmation respects the durability setting, and fallback cannot be hidden by the next shared operation. | [SharedDurability_Tests](../LiteDB.Tests/Internals/SharedDurability_Tests.cs): observed device syncs, automatic/explicit commits, encrypted wrappers, injected unsupported sync, retry and connection-local diagnostics. |
| Lost/torn new writes cannot damage the previously acknowledged prefix or expose a partial transaction. | [SharedCommitFailure_Tests](../LiteDB.Tests/Internals/SharedCommitFailure_Tests.cs): durable/volatile file images, lost writes, later sectors persisting ahead of a torn frame, failure after successful sync, and a successful-sync control. Covers BSON/compact, plain/encrypted, automatic/explicit commits; repeated shared recovery checks full documents, indexes and an untouched collection. A foreign thread verifies writer-mutex release. |
| Process death preserves acknowledged transactions, discards unconfirmed safepoints and preserves another process's snapshot. | [SharedStorageProcess_Tests](../LiteDB.Tests/Internals/SharedStorageProcess_Tests.cs): actual killed writers, proven nonempty/unconfirmed WAL, full payload/index checks and checkpoint after readers drain. |
| Damaged data fails with a page diagnostic; damaged WAL follows the verified-prefix recovery policy and reports discarded bytes. | The same process suite checks repeated data-page rejection, read-only data/WAL byte preservation, repeated recovery and checkpoint/reopen across BSON/compact and plain/encrypted files. |

Run the focused suite with `TestingEnabled=true` and `tests.runsettings`:

```sh
dotnet test LiteDB.Tests -c Release -f net8.0 -p:TestingEnabled=true --settings tests.runsettings --filter 'FullyQualifiedName~SharedSafetyProcess|FullyQualifiedName~SharedStorageProcess|FullyQualifiedName~SharedDurability|FullyQualifiedName~SharedCommitFailure'
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
