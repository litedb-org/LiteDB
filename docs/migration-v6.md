# Upgrading from LiteDB 5.x to v6

This guide covers the Phase 1–4 v6 stack and its migration contract. Keep an
offline backup before the first writable v6 open: data/WAL conversion and index
migration can make files unreadable by 5.x. Returning to 5.x requires restoring
that backup, rather than opening the upgraded files with an old executable.
See [format compatibility](rules/compatibility.md) and
[index migration](release-notes.md#index-ordering-migration-format-v11).

## Default durability and performance

`DurableCommits` defaults to `true`. A successful commit waits for a device
barrier covering its WAL and dependencies; group commit can cover several
writers with one barrier. This costs latency compared with 5.0.21's OS-cache
acknowledgement. Prefer batching related writes in one transaction. See
[group commit](group-commit.md) and its
[durability tests](../LiteDB.Tests/Internals/GroupCommit_Tests.cs).

Historical measurements below are **milliseconds for 5,000 single-document
auto-commit inserts**, medians from the Phase 1 `02c862730` hosted
[benchmark run 42381473312](https://github.com/litedb-org/LiteDB/actions/runs/42381473312).
They describe those runners/revision, not a final-v6 guarantee. The log's
`dev-default` means durable; `dev-os-cache` means `DurableCommits=false`.

| OS/runtime | 5.0.21 | v6 durable default | v6 OS-cache opt-out |
| --- | ---: | ---: | ---: |
| Linux .NET 8 | 493.84 | 2257.09 | 733.18 |
| Linux .NET 10 | 396.13 | 2480.70 | 1855.93 |
| Windows .NET 8 | 859.56 | 28452.62 | 4887.09 |
| Windows .NET 10 | 756.83 | 27768.60 | 4223.06 |
| macOS .NET 8 | 566.36 | 5019.52 | 667.06 |
| macOS .NET 10 | 472.01 | 3748.02 | 653.33 |

Hosted spread was substantial (especially macOS); the run failed the Linux
.NET 10 bulk gate at 1.158× against 1.10×, with observed bounds 0.982–1.239×.
The [Phase 1 report](https://github.com/litedb-org/LiteDB/pull/3131) and
[benchmark methodology](../tools/Ab5021Benchmarks/README.md) explain the scope.
Single insert versus 5.0.21 is report-only, including with durability disabled:
crash-recoverable checkpoints still have a cost. This is not a passed overall
v5 performance gate; the bulk gap remains [issue 3145](https://github.com/litedb-org/LiteDB/issues/3145).

To choose the earlier, weaker contract, set `durable commits=false` in the
connection string or `DurableCommits = false` on `ConnectionString` or
`EngineSettings`. Commits then reach the OS cache and can survive process death,
but are not guaranteed to survive power loss. Unsupported device sync also
uses the documented weaker contract (`$database.durableLogFlush=false`);
actual I/O failures stop the engine. Encrypted WAL preambles require successful
sync and have no unsupported-sync fallback. See
[durability opt-out and diagnostics](connection-string-parsing.md#opting-out-of-durable-commits-2818),
[device-sync behavior](release-notes.md#durable-commits-2818) and
[downgrade tests](../LiteDB.Tests/Internals/GroupCommitUnsupported_Tests.cs).

Background checkpointing is **off** ([3143](https://github.com/litedb-org/LiteDB/issues/3143));
checkpoint protocol option B is deferred ([3134](https://github.com/litedb-org/LiteDB/issues/3134)).
Neither decision disables ordinary automatic or explicit checkpoints.
See [checkpoint publication](header-publication.md).

## Direct connections share a process host

Compatible file-backed Direct connections to one physical database share a live
storage engine per loaded LiteDB assembly. Each facade owns its transactions;
ordinary peer calls do not join another facade's transaction. Settings must be
compatible, pragmas affect the shared host, and mapper state stays facade-local.
Native admission excludes incompatible Direct/Shared participants. Hard links
and aliases with separate WALs are refused; symlinks canonicalize. Memory,
temporary databases and custom streams retain their separate storage path.
See [Direct process host](direct-process-host.md) and
[host tests](../LiteDB.Tests/Engine/DirectProcessHost_Tests.cs).

Dispose every facade deterministically. Disposing one does not close its peers;
the final release closes storage. Readers do not keep a Direct host alive after
all facades close. Manual checkpoint/rebuild requires one facade, so close peer
facades first. A fatal engine failure affects all attached facades: release them
and reopen for recovery. See [host lifetime and maintenance](direct-process-host.md#settings-and-maintenance).

## Shared coordination, platforms and fallback

File-backed Shared connections default to ABI-2 coordination in
`<canonical-database>-shm` on qualified .NET 8+ Linux x64/ARM64, Windows x64 and
macOS ARM64/x64. This is a coordination format, separate from the data/WAL
formats. Qualified default opens no longer create the legacy `-readers` or
`-shared-*` families. x86/.NET Framework and other unqualified targets use
legacy coordination. Native-admitted volumes outside the mapped allowlist,
including tmpfs/overlay, also fall back to legacy; network/unknown volumes
remain refused by native admission. Streams/memory/temp retain legacy behavior.
See [default policy](shared-one-file-prototype.md),
[platform/default tests](../LiteDB.Tests/Engine/SharedOneFileDefault_Tests.cs) and
[qualification report](https://github.com/litedb-org/LiteDB/pull/3146).

Use `Shared One File=false` or `EngineSettings.SharedOneFile=false` for the v6
legacy opt-out. Explicit `true` requires a qualified target, volume and physical
file; it refuses unsupported requests rather than silently falling back.
Legacy coordination remains frozen in v6 and is scheduled for removal in v7.
All participants must select the same family and canonical identity. ABI-2
facades must match the settings listed in the
[Shared contract](shared-one-file-prototype.md); automatic default selection
allows read-only/writable facades to coexist, whereas explicit ABI-2 opt-in
retains strict ReadOnly matching. See
[default availability guards](../LiteDB.Tests/Engine/SharedOneFileDefaultAvailability_Tests.cs).

### Offline upgrade procedure

1. Stop **every** application, service, worker and tool using the database,
   including idle facades and outstanding readers. Replace all old executables
   before reopening. Concurrent mixed old/new deployments are unsupported;
   older binaries may not implement native admission. See
   [offline transition](shared-one-file-prototype.md#offline-coordination-transition).
2. Preserve an offline backup of the data file and its matching WAL. A checkpoint
   can leave a nonempty WAL while readers exist; a data-only backup requires
   writer/checkpoint exclusion and a verified empty WAL. Otherwise capture the
   pair in one filesystem snapshot. Never copy the two live files independently.
   See [backup rules](release-notes.md#snapshot-aware-checkpoints-format-v13).
3. Check target/runtime/volume qualification and agree on default versus legacy
   opt-out across all participants. Provide directory permissions and space for
   coordination, WAL and index migration. See
   [Shared policy](shared-one-file-prototype.md) and
   [index migration budget](release-notes.md#index-ordering-migration-format-v11).
4. Open once writable with v6 and the selected Shared settings. Default mapped
   admission automatically validates and retires recognised ABI-1 remnants under
   exclusive native admission and exclusive DMS (the mapped liveness lock).
   Retirement never writes data/WAL and never forces a rebuild. Normal storage
   recovery and index conversion follow. See
   [retirement implementation](../LiteDB/Client/Shared/SharedCoordinationFilesOfflineUpgrade.cs).
5. If admission fails, close remaining participants or correct the reported
   permissions/I/O problem, then retry. Each retirement deletion is independently
   retryable, including after another interrupted retry. Unknown names, formats,
   capabilities or symbolic links are preserved and refused: investigate them,
   never force-delete sidecars to bypass admission. See
   [offline upgrade tests](../LiteDB.Tests/Internals/SharedOfflineUpgrade_Tests.cs).
6. Verify application documents and indexed lookups, close, then reopen with all
   v6 participants using the same protocol/settings. Keep the original backup
   for rollback. Sequential reuse by native-admission-aware legacy clients is
   supported after all mapped clients close (subject to data-format compatibility);
   the next mapped open repeats remnant retirement. This does not permit a
   concurrent mixed-version rollout. See [offline transition](shared-one-file-prototype.md#offline-coordination-transition).

A default read-only open with legacy remnants stays on protected legacy access
without retiring them; close it before the writable mapped transition. Explicit
ABI-2 read-only selection refuses retirement. Rebuild/mapped maintenance needs
the **only participant**: close other processes, idle facades and query results.
Refusals include `IOException`, `PlatformNotSupportedException` and
`InvalidOperationException`; many have no LiteDB numeric code. The table below
distinguishes these from coded storage/transaction errors. See
[Shared contract](shared-one-file-prototype.md) and
[maintenance guards](../LiteDB/Client/Shared/SharedMemoryMaintenance.cs).

## Migrate to transaction handles

Legacy `BeginTrans`, database `Commit` and database `Rollback` remain callable
but are obsolete (CS0618). Replace the boolean begin with `using var transaction
= db.BeginTransaction()`, obtain **every** transactional collection/query from
that handle, and complete the handle itself. Ordinary database collections stay
independent. `ILiteDatabase` providers need the optional
`ILiteTransactionProvider` capability; unsupported providers refuse before
starting work. See [API and migration details](transaction-handles.md).

Each C# block in this guide is a complete program compiled with warnings as
errors and executed by the [Phase 4 compatibility runner](../scripts/test-transaction-handle-compatibility.py).
This example commits on another thread only after the preceding call returns;
thread handoff is sequential, not concurrent use.

```csharp
using System;
using System.Threading;
using LiteDB;

using var db = new LiteDatabase(":memory:");
using (var transaction = db.BeginTransaction())
{
    var rows = transaction.GetCollection("orders");
    rows.Insert(new BsonDocument { ["_id"] = 1, ["status"] = "paid" });
    Exception failure = null;
    var worker = new Thread(() =>
    {
        try { transaction.Commit(); }
        catch (Exception error) { failure = error; }
    });
    worker.Start();
    worker.Join();
    if (failure != null) throw failure;
    if (transaction.State != LiteTransactionState.Committed) throw new Exception("Commit failed");
}
using (var transaction = db.BeginTransaction())
    transaction.GetCollection("orders").Insert(new BsonDocument { ["_id"] = 2 });
if (db.GetCollection("orders").Count() != 1) throw new Exception("Dispose must roll back");
Console.WriteLine("PASS documentation examples");
```

Dispose active handles to roll back; dispose bound readers before commit.
Overlapping/reentrant calls are rejected and completed bound objects cannot be
reused. Dropping a reference or ending the creator thread does not release an
active handle retained by a live database. There is no inactivity auto-rollback,
async API or ambient flow. SQL, file storage, drop/rename, maintenance and pragma
mutation are outside the handle API. See
[lifetime](transaction-handles/lifetime.md) and
[binding tests](../LiteDB.Tests/Engine/TransactionHandleBinding_Tests.cs).

**Q1: throw 141.** A peer `BeginTransaction()` behind this thread's legacy
transaction throws `SAME_THREAD_TRANSACTION` immediately. Same-thread conflicting
legacy-owner operations always refuse; there is no wait opt-in. Complete legacy
work on its owning thread first. Two independent handles instead use cooperative
waits and can time out (120); do not wait for yourself without another thread
able to complete the holder. See [rules and executable Q1 example](transaction-handles.md).

**Q2: reserve the writer.** A Shared handle reserves writer ownership throughout
its active lifetime, even if it only reads. Other writers wait according to
`Timeout`/`TIMEOUT`. Keep handles short. The following program checks timeout
and peer progress after rollback; its temporary directory is application-owned.
See [Shared tenure tests](../LiteDB.Tests/Internals/TransactionHandleSharedTenure_Tests.cs).

```csharp
using System;
using System.IO;
using LiteDB;

var directory = Path.Combine(Path.GetTempPath(), "litedb-migration-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    var connection = new ConnectionString
    {
        Filename = Path.Combine(directory, "shared.db"),
        Connection = ConnectionType.Shared
    };
    using var db = new LiteDatabase(connection);
    using var peer = new LiteDatabase(connection);
    peer.Timeout = TimeSpan.FromSeconds(1);
    using (var transaction = db.BeginTransaction())
    {
        _ = transaction.GetCollection("orders").Count();
        try
        {
            using var blocked = peer.BeginTransaction();
            throw new Exception("Expected timeout behind read-only work");
        }
        catch (LiteException error) when (error.ErrorCode == LiteException.LOCK_TIMEOUT) { }
        transaction.Rollback();
    }
    using (var next = peer.BeginTransaction()) next.Commit();
    Console.WriteLine("PASS documentation examples");
}
finally
{
    Directory.Delete(directory, true);
}
```

### Commit uncertainty

With durability enabled and supported, returned `Commit()` acknowledges the full
transaction. A crash after durable WAL confirmation but before return can still
recover it committed. `State == Indeterminate` after a failure means establish
the outcome by recovery/readback before retrying; use an application operation
identifier to avoid duplicates. State can also be Indeterminate while Commit is
in flight: it does not permit reuse. A dead process cannot report a state.
Unacknowledged work at the confirmation/barrier boundary can recover completely
present or absent; it must never recover partially. Process death preserves OS
cache and is distinct from power loss. See [commit contract](transaction-handles.md)
and [commit-cut tests](../LiteDB.Tests/Internals/TransactionHandleReviewSafety_Tests.cs).

## Breaking changes and fixes

| Change | Application fix | Evidence/details |
| --- | --- | --- |
| Durable default increases commit latency | Batch writes; choose `DurableCommits=false` only with its weaker guarantee | [Durability](#default-durability-and-performance) |
| Shared protocol/participant lifetime changes | Stop/upgrade all executables offline, align family/settings; close every participant for maintenance | [Shared migration](#offline-upgrade-procedure) |
| Direct facades share storage and enforce settings | Align host settings; finish owner transactions before peer use; close peer facades for maintenance | [Direct host](direct-process-host.md) |
| Conflicting same-thread legacy work throws 141; removed `WaitForSameThreadTransaction` option | Remove the option and finish legacy work first, or migrate to handles | [Parser/ownership contract](connection-string-parsing.md#same-thread-transaction-writes) |
| Legacy API CS0618 warnings | Use handles; temporary bridge: `<WarningsNotAsErrors>CS0618</WarningsNotAsErrors>` | [Handle migration](transaction-handles.md) |
| Pre-existing `ILiteEngine.EnsureVectorIndex` ABI break for 5.0.21-compiled implementers | Recompile implementers/decorators and implement/forward the vector member; unchanged client callers are a separate tested case | [ABI limitation](release-notes.md#known-pre-existing-custom-engine-abi-break) |
| Automatic checksums/index-order migration makes upgraded files unreadable by older engines | Back up before writable open; restore backup for old-version rollback; read-only legacy access can use `legacy index scan=true` | [Formats](rules/compatibility.md) |
| Exact numeric equality and corrected index ordering | Use one numeric type per field; compare decimal fields with decimal parameters, and resolve unique collisions before migration | [Numeric comparison](release-notes.md#breaking-change-exact-numeric-comparison) |
| `BsonValue` copies CLR containers and hashes their contents | Mutate the BSON container itself; avoid mutating values used as hash keys | [BSON behavior](release-notes.md#bsonvalue-clr-collection-compatibility) |
| Caller streams remain caller-owned | Dispose streams after database disposal; retain data and log together when using a writable caller log | [Stream ownership](release-notes.md#stream-ownership-change) |
| Raw equals-sign paths and unknown single `key=value` inputs are filenames | Use a `ConnectionString` object for arbitrary paths; verify option spelling | [Parsing](connection-string-parsing.md) |
| Checkpoint can retain WAL | Verify empty WAL under exclusion before data-only backup, or snapshot the pair | [Checkpoint/backup](release-notes.md#snapshot-aware-checkpoints-format-v13) |

## Troubleshooting

Numeric constants below are defined in [LiteException](../LiteDB/Utils/LiteException.cs).
Use the exception message as well as its type/code; admission errors often have
**no** LiteDB error code.

| Error | Typical migration trigger | Action and supporting contract |
| --- | --- | --- |
| 103 `INVALID_DATABASE` | Invalid legacy WAL/header relationship or unsupported input | Preserve data/WAL together, investigate; do not force conversion. [Legacy WAL validation](release-notes.md#legacy-wal-conversion-5x-files) |
| 0 (message-specific) | Read-only index migration required, or migration capacity exceeds LIMIT_SIZE | Open writable once, use read-only legacy scan, or raise the explicit migration budget as the message directs. [Migration admission](../LiteDB/Engine/Engine/IndexMigration.cs), [capacity](../LiteDB/Engine/Engine/IndexMigrationCapacity.cs) |
| 110 `INDEX_DUPLICATE_KEY` | Corrected ordering reveals unique collisions | Correct source keys using a compatible workflow before migration. [Preflight](../LiteDB.Tests/Engine/IndexMigrationPreflight_Tests.cs) |
| 120 `LOCK_TIMEOUT` | Held Shared writer/handle, or legacy WAL still needed by readers | Finish/dispose holders; close readers before offline retry. [Handles](transaction-handles.md), [legacy WAL](release-notes.md#legacy-wal-conversion-5x-files) |
| 138 `UNSUPPORTED_FILE_VERSION` | Unsupported data-file version | Preserve files and use an engine supporting that version; do not force a downgrade. [Formats](rules/compatibility.md) |
| 139 `REBUILD_INCOMPLETE` | Interrupted replacement/rebuild | Retain recovery files and follow reported recovery instructions. [Publication](header-publication.md) |
| 140 `CHECKSUM_MISMATCH` | Damaged checksummed data | Preserve evidence and restore a verified backup; do not suppress integrity validation. [Checksums](page-and-wal-checksums.md) |
| 141 `SAME_THREAD_TRANSACTION` | Peer conflicts with this thread's legacy owner (including Q1) | Complete owner on its thread before peer work. [Ownership](transaction-handles.md) |
| `IOException` (no numeric code) | Mixed admission families, live participant during retirement, unknown/aliased remnants, failed deletion, unsupported native volume or terminal I/O failure | Read message/inner exception. Close participants; correct I/O; retry only recognised offline transition. Never delete unknown remnants. [Admission](../LiteDB/Client/Shared/NativeAdmission.cs), [retirement](../LiteDB/Client/Shared/SharedCoordinationFilesOfflineUpgrade.cs) |
| `PlatformNotSupportedException` (no numeric code) | Explicit mapped request on unsupported target/volume | Use qualified storage/runtime or the v6 legacy opt-out. [Platform policy](shared-one-file-prototype.md) |
| `InvalidOperationException` (no numeric code) | Incompatible host settings, maintenance with peers, overlapping/completed handle use | Align settings, close peers/results, serialize handle calls. [Host](direct-process-host.md), [handles](transaction-handles.md) |
| `TypeLoadException` | Old `ILiteEngine` implementation lacks vector method | Recompile and implement/forward `EnsureVectorIndex`. [ABI limitation](release-notes.md#known-pre-existing-custom-engine-abi-break) |

## Known limits and remaining release work

The [Phase 3 qualification](https://github.com/litedb-org/LiteDB/pull/3146) and
[Phase 4 qualification](https://github.com/litedb-org/LiteDB/pull/3147) establish
bounded runtime/platform and failure-model evidence, not arbitrary-filesystem or
physical-device/VM power-reset qualification. Their inherited quarantines and
reader-lease fault-model limits remain. Directory-entry persistence remains
[3011](https://github.com/litedb-org/LiteDB/issues/3011); ambiguous legacy WAL
ownership is [3043](https://github.com/litedb-org/LiteDB/issues/3043), and custom
stream guarantees are bounded by [3044](https://github.com/litedb-org/LiteDB/issues/3044).
Callbacks can delay database close indefinitely. There is no async/ambient
transaction API in this stack. See [handle lifetime](transaction-handles/lifetime.md).

Release-wide safety campaigns and final all-OS performance acceptance remain
separate Phase 5 work in [roadmap 3124](https://github.com/litedb-org/LiteDB/issues/3124).
Bulk performance [3145](https://github.com/litedb-org/LiteDB/issues/3145) remains
open; single insert is report-only; background checkpoint stays off
([3143](https://github.com/litedb-org/LiteDB/issues/3143)); checkpoint option B
stays deferred ([3134](https://github.com/litedb-org/LiteDB/issues/3134)).
