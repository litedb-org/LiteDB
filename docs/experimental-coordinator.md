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
- Readers open snapshots of the files directly. The coordinator publishes what they need on a
  shared-memory status page, so most reads involve no IPC at all.

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

A read outside an explicit transaction uses a direct snapshot: a read-only engine on the data
and WAL files, cached by the client. In the common cases it takes no IPC at all. The
coordinator publishes its state on a **status page**, a 4 KiB memory-mapped file in the per-user
temp directory named after the database path (`litedb-coord-<sha1(path)>.page`, owner-only on
Unix). Every coordinator of a database writes the same page. A graceful stop marks it "no
coordinator" and deletes it; a killed coordinator leaves it, and its successor overwrites it.

The page holds, under a seqlock (the sequence is odd while it changes, and readers retry until
they see the same even value before and after):

| Field | Meaning |
| --- | --- |
| instance | random id of the running coordinator; 0 after a graceful stop |
| version | read version of the newest committed transaction, published before the commit returns |
| structural | incremented before and after every checkpoint (before its lease scan), format promotion, header invalidation and the coordinator's own open, so it is **odd while one runs** |
| reuse epoch | incremented before a reclaimed WAL slot is overwritten |
| resets | incremented when the read version decreases (the WAL was truncated) |

A client decides as follows, in order:

1. **Fast path.** If its cached snapshot has the page's instance, version and resets, nothing was
   committed since, so the cached engine answers. No IPC, no file access.
2. **Incremental refresh.** If only commits were appended since the snapshot's last WAL scan
   (same instance, same even structural counter, same resets and reuse epoch; newer version), and
   no reader uses the cached engine, the client registers a lease for the new version and advances
   the engine by reading only the WAL frames from its rescan point on. The rescan point is the end
   of the last confirmed transaction, or the first frame of a transaction that was still open at
   the last scan. The page is re-read afterwards (see the proof). A rejected refresh retires the
   snapshot.
3. **Handshake.** Otherwise it opens a new snapshot without the coordinator: read the page (the
   structural counter must be even), register an OS-held lease file for the published version,
   open the read-only engine, re-read the page, and accept only if instance, structural counter
   and resets are unchanged and the engine opened at exactly that version. It retries twice.
4. **Grant over IPC.** As before this change: the coordinator closes its gate (no engine call runs),
   returns its read version, and reopens the gate once the client has opened its engine. It is
   used when there is no page, or the handshake keeps losing to checkpoints.

A client still reads **over IPC** when a grant cannot close the gate within 250 ms, the lease
cannot be registered, the read is inside an explicit transaction, or the query writes (`FOR
UPDATE`, `INTO`). IPC results are materialized on the coordinator and sent in chunks of at most 8 MiB of rows,
so no reply frame exceeds the 64 MiB message limit.

Leases registered by the fast paths skip the registry scan and are deleted when their handle
closes, so closed leases do not pile up. The coordinator's scan still fails closed if the
registry cannot be read.

### Why the snapshot is safe

A snapshot at version V is safe if no frame V needs is reclaimed while it is read. A checkpoint
keeps the newest version, every version it saw leased, and the floors of both; everything
else may be cleared and reused. The client orders its steps as *register lease(V) → fence →
re-read page*; the coordinator orders its checkpoint as *structural odd → fence → scan leases →
… → structural even*. Both pairs are separated by full fences, and the lease file is created
by a syscall that completes before the client's re-read.

| Interleaving | Outcome |
| --- | --- |
| The checkpoint marks structural odd before the client's re-read | The re-read sees a changed counter (or odd): rejected, lease dropped. |
| The checkpoint marks structural odd after the client's re-read | Its lease scan follows its mark, which follows the client's registration: it sees lease(V) and keeps V. |
| A checkpoint completed before the client first read the page | V was the newest version when read, so that checkpoint kept everything V needs: V's state is the one it kept plus later commits, whose frames it could not touch. |
| A checkpoint ran during the engine open | The counter changed between the two reads: rejected, so an open never overlaps a backfill, clearing, truncation or header write. |
| A commit completed during the open | The engine lands above V: rejected by the version check, because only V is leased. |
| A commit was in progress during the open | Its frames are unconfirmed, or torn if a reused slot was being written. Read-only recovery ignores them, or stops before them (then the version check rejects). |
| Format promotion or header invalidation during the open | Bracketed like a checkpoint: rejected. |
| The coordinator opens (recovers, migrates) | It publishes a new instance with an odd counter **before** opening its engine: no client accepts a snapshot across it. |
| The coordinator is killed | The page keeps its last state, which stays accurate because nobody writes until a successor, whose first write (new instance, odd) invalidates every cached snapshot and handshake. A kill during a checkpoint leaves the counter odd, so clients use IPC and re-elect. Lease files outlive the coordinator, and the successor's checkpoints honor them. |
| The coordinator stops gracefully | Instance 0: clients stop trusting the page and re-elect over IPC. |
| Refresh: a checkpoint runs while the tail is read | Same argument as the handshake, with the new version's lease registered before the read and the page re-read after it. |
| Refresh: a reclaimed slot was reused since the last scan | Refused by the reuse epoch: the new transaction's frames could lie before the rescan point. As a second barrier, a confirmation whose frame count or digest does not match the frames seen fails recovery, so the refresh is rejected. |
| Cached snapshot reuse | Same instance, version and resets: nothing committed. The snapshot's own lease rules out the WAL truncation that could reuse a version number. |

Tests force each step of the handshake and of refresh against a checkpoint, and cross-check every
advanced snapshot against a fresh open. Two negative controls exist:
- Removing the re-read lets a paused checkpoint reclaim the accepted snapshot's frames, and reading it fails.
- Removing the reuse check turns a reuse scenario into a refresh that recovery rejects.

The earlier negative control remains: removing the lease file makes
`A_direct_snapshot_keeps_its_version_while_the_coordinator_writes_and_checkpoints` fail.

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
- Only the IPC grant fallback stalls writers (for one read-only engine open). A cached snapshot
  or a long reader holds back WAL reclamation like any leased reader.
- The status page trusts that a coordinator which cannot create or open it also leaves no stale
  page behind that clients still map. A graceful stop marks the page "no coordinator", and a
  crashed coordinator's page is overwritten by the next one. A successor that cannot write the
  page at all (for example after its permissions changed) while clients still map a crashed
  predecessor's page is not detected. Clients then read that predecessor's last snapshot until
  their next IPC call fails.
- A refreshed snapshot keeps the pragmas object it opened with (lock timeouts, for example); the
  header's collections, pages and file version are refreshed in place.
- The status page is a 4 KiB file in the temp directory. It is deleted on a graceful stop. On
  Windows the deletion completes once no client maps it; until then a new coordinator cannot
  create the page and serves snapshot grants over IPC.
- On Unix the page lives in `<base>/litedb-coord-<user>/`, a real directory (not a symlink) with
  mode 0700. The base is the first of these that no group or other account can write to:
  `$TMPDIR` or `$XDG_RUNTIME_DIR`, and for root `$TMPDIR`, `/run` or `/var/run`. The shared `/tmp`
  (1777) never qualifies. A mode check there would not be enough, because a directory another
  account planted stays under its control and can change mode after the check. Without a
  qualifying base (for example, a non-root service with neither variable set) there is no page
  and snapshots use grants over IPC. On Windows the page inherits the per-user temp directory's ACL.
- Lease files require write access to the database directory.
- Documents stream one per round trip, so bulk inserts pay per-document IPC latency.
- Tested on Windows (.NET 8 and .NET 10) only. The Unix socket cleanup after a killed coordinator
  has not been exercised.

## Measurements

### Reads without IPC (status page, handshake, incremental refresh)

Same machine and harness. "Before" is the IPC-registered snapshot of `7f9544af2`, "after" this
change; three interleaved runs each, ms per operation. Shared and direct mode are single runs of
the same build, for reference.

| Scenario | Before | After | Shared | Direct |
| --- | --- | --- | --- | --- |
| Point read `FindById` (4000) | 0.271–0.301 | 0.040–0.048 | 0.747 | 0.037 |
| 1 update per 9 point reads (2000) | 0.902–0.935 | 0.315–0.364 | 1.462 | 0.156 |
| Full scan of 2,000 rows (200) | 4.97–5.17 | 4.19–4.40 | 9.29 | 4.27 |
| Update loop (1000) | 1.89–1.96 | 1.97–2.08 | 2.90 | 1.34 |
| Update while another connection holds a reader (500) | 1.88–2.09 | 1.77–2.01 | 5.81 | not possible |

- **Point reads:** a point read before this change cost one pipe round trip (about 0.12 ms, the
  "same" check) plus the local read. Now the page check replaces the round trip, and a point read
  runs at direct-mode speed.
- **Opening a new snapshot** without the cache cost about 3.4 ms: 0.8 ms lease registration with a
  registry scan, 2.5 ms engine open, and the round trips. A refresh costs about 0.6 ms: 0.34 ms to
  create the new lease file, 0.18 ms to scan the appended frames, and 0.11 ms to close the old lease.
- **Mixed workload:** in the 1:9 mix, 199 of 200 post-commit reads refreshed and one opened; the
  remaining time is the updates themselves.
- **Writes** are unchanged within noise. The page write per commit and per reused slot is a few
  stores under a lock.

### Initial prototype

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

- **Fuzz targets** with coordinator crash points (grant, handshake steps, refresh, ack, streaming
  insert). The proof above is a written argument backed by forced interleavings, not a model check.
- **Tests of the Unix transport** (Linux and macOS), including stale socket cleanup and abandoned
  named-mutex behavior.
- **Batched streaming** for documents that already carry an `_id`, and streaming IPC results.
- **Participant exclusion:** a way to refuse non-coordinated writers, for example a marker the
  coordinator holds that shared and direct mode check.
