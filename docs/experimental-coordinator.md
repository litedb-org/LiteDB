# Experimental: coordinator mode

> **Status: experiment.** This prototype is opt-in only. It does not change any default,
> connection-string key or the file format. Its API is marked `[Experimental]`: suppress the
> `LITEDB_EXPERIMENTAL_COORDINATOR` diagnostic to use it. It is available on .NET 8+ only
> and is not covered by fuzzing yet. Do not use it in production.

Shared mode serializes processes with a named mutex. Every operation opens an engine
(file opens, header validation, WAL index build), runs, and closes it again. Coordinator
mode tests the alternative from #3004:

- One process owns the database and keeps the only writable engine open.
- Other processes send writes to it over IPC.
- Readers register a snapshot with it over IPC, but then read the files directly.

```csharp
#pragma warning disable LITEDB_EXPERIMENTAL_COORDINATOR
using var db = new LiteDatabase(new CoordinatedEngine("data.db"));
```

## Roles and election

- **Coordinator:** the first `CoordinatedEngine` to acquire the per-database coordinator mutex
  (`coord-<sha1(path)>`, created like shared mode's mutex). It opens a direct-mode `LiteEngine`
  and serves a named pipe `litedb-coord-<sha1(path)>`. On Linux and macOS, .NET implements the
  pipe as a Unix domain socket. The pipe is restricted to the current user (`PipeOptions.CurrentUserOnly`).
- **Client:** every other `CoordinatedEngine`, in the same process or in another one. Each client
  thread uses its own pipe session, which matches the engine's thread-bound transactions.
- **Election:** runs on first use and after a lost coordinator. The engine tries the mutex (an
  abandoned mutex counts as acquired), otherwise connects to the pipe, and retries for up to
  20 s. A new coordinator opens its engine only after it owns the mutex, so WAL recovery runs
  as after any crash.

**Every process opening the file must use `CoordinatedEngine`.** A concurrent direct-mode or
shared-mode writer is not coordinated. On Unix, file sharing is advisory, so such a writer could
corrupt the database.

## Writes

Requests are length-prefixed BSON documents. The coordinator runs each request on the session's
thread, so its commits are durable before the reply returns, as in direct mode.

- **Supported operations:** `BeginTrans`, `Commit`, `Rollback`, `Insert`, `Upsert`, `Update`,
  `UpdateMany`, `Delete`, `DeleteMany`, index DDL, collection DDL, pragmas and `Checkpoint`.
- **Streamed documents:** `Insert`, `Upsert` and `Update` stream documents one at a time. Before
  pulling the next document, the coordinator returns the previous document's final `_id`, so the
  Mapper's generated-ID handoff (the entity's `Id` property) behaves exactly as with a local
  engine. This costs one round trip per document.
- **Closed sessions:** a closed session rolls back its transaction on the coordinator.

## Reads

A read outside an explicit transaction uses a direct snapshot:

1. The client asks for a snapshot on its lease connection. It includes the version of its cached
   snapshot, if it has one.
2. If nothing was committed since that version, the coordinator answers `same`, and the client
   reuses its cached read-only engine.
3. Otherwise the coordinator waits until no engine call is running, closes its **gate** (no
   commit, safepoint or checkpoint can start), and returns its committed read version.
4. The client creates an OS-held lease file for that version: the same `-readers` registry that
   shared mode uses, which also feeds the coordinator's `SharedReaderVersions`. It then opens a
   read-only snapshot engine on the data and WAL files.
5. The client checks that the engine opened at exactly the granted version, and tells the
   coordinator, which reopens the gate.
6. The query runs against the local snapshot engine, with no further IPC. The cached snapshot is
   released after 0.5 s without use.

A client falls back to reading **over IPC** in these cases:
- the gate cannot be closed within 250 ms (a long write is running);
- the lease cannot be registered;
- the read is inside an explicit transaction, so it must see the transaction's uncommitted writes;
- the query writes (`FOR UPDATE`, `INTO`).

IPC results are materialized on the coordinator.

### Why the snapshot is safe

| Property | Mechanism |
| --- | --- |
| The snapshot engine opens on a consistent committed state | The gate excludes every engine call during the open, and the client verifies `ReadVersion == granted`. |
| Checkpoint never moves or reclaims frames a snapshot needs | The OS-held lease file feeds `SharedReaderVersions`, the same v13 protocol as shared-mode readers. |
| A coordinator crash does not orphan snapshots | Lease files are owned by the reading process, and the next coordinator's checkpoints see them. |
| Reusing a cached snapshot is equivalent to a fresh one | The version is unchanged, and the snapshot's own lease prevents the WAL truncation that could reuse a version number. |

The negative control that removes the lease file makes
`A_direct_snapshot_keeps_its_version_while_the_coordinator_writes_and_checkpoints` fail: the
checkpoint truncates the WAL under the reader.

## Failure semantics

- **Coordinator exits cleanly:** clients see the pipe close. The next call elects a new coordinator.
- **Coordinator is killed:**
  - Every acknowledged write was committed and survives.
  - A write whose reply was lost fails with *"outcome is unknown"*. It may or may not have
    committed, and it is **not** retried automatically.
  - An open explicit transaction fails with *"transaction was aborted"*.
  - Reads are retried after re-election.
- **Client is killed:** its sessions close, and the coordinator rolls back their transactions.
  Its lease files are released by the OS.

## Limitations

- Mixed participants are unsupported (see above), and so are cross-user and cross-machine access.
- `Rebuild` is not supported. Vector index creation must run in the coordinator process. Vector
  queries inside a client transaction are not supported.
- A snapshot grant briefly stalls writers, for the duration of the read-only engine open (about a
  millisecond on NVMe). A cached snapshot or a long reader holds back WAL reclamation like any
  leased reader.
- Lease files require write access to the database directory.
- Documents stream one per round trip, so bulk inserts pay per-document IPC latency.
- Tested on Windows (.NET 8 and .NET 10) only. The Unix socket cleanup after a killed coordinator
  has not been exercised.

## Measurements

Windows 11, NVMe, .NET 10, Release. The coordinator is a separate process; the measured process
is a client. The database has 2,000 rows of about 260 bytes. Shared mode is branch `shared/salvage`
with #3004's lazy close checkpoint. Figures are ms per operation over three interleaved runs.

| Scenario | Shared | Coordinator client | Direct |
| --- | --- | --- | --- |
| Update loop (500) | 4.2–5.0 | 2.0–2.2 | 1.2–2.2 |
| Insert loop (500) | 4.5–5.2 | 2.4–3.9 | 1.7–2.8 |
| Point read `FindById` (1000) | 1.60–1.65 | 0.32–0.37 | 0.05 |
| 1 update per 9 point reads (1000) | 2.8–2.9 | 1.0–1.5 | 0.20–0.22 |
| Full scan of 2,000 rows (50) | 11.5–13.2 | 5.3–8.1 | 4.9–5.2 |
| Update while another connection holds a reader (300) | 7.5–8.0 | 2.2–2.5 | not possible |
| Point read over IPC, inside a transaction (1000) | — | 0.34–0.47 | — |
| Full scan over IPC, inside a transaction (50) | — | 12.0–17.6 | — |

Takeaways:
- Writes cost about half of shared mode. The remainder is the commit sync plus one or two pipe round trips.
- Point reads are 4–5× faster than shared mode, because the cached snapshot is reused.
- Scans read the files directly at close to direct-mode speed. The same scan over IPC costs 2–3×
  more, because the result is serialized.
- Without the snapshot cache, a direct snapshot per point read cost 2.6 ms, slower than shared mode.
  Opening a read-only engine dominates, as #3004 found.

## What production use would still need

- **A proof table** in the style of `storage-stack-safety.md` for the gate and for snapshot reuse,
  plus fuzz targets with coordinator crash points (grant, open, ack, streaming insert).
- **Tests of the Unix transport** (Linux and macOS), including stale socket cleanup and abandoned
  named-mutex behavior.
- **Batched streaming** for documents that already carry an `_id`, and streaming IPC results.
- **Participant exclusion:** a way to refuse non-coordinated writers, for example a marker the
  coordinator holds that shared and direct mode check.
