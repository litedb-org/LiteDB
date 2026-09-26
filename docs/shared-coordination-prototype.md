# Same-version Shared coordination prototype

This is an isolated experiment, not an enabled production change. All concurrent
Shared participants use exactly the same LiteDB version. Mixed-version admission
is not a proof obligation for this task; persisted file compatibility still is.

The first candidate removes mutex admission when reusing cached read state.
An idle cache retains no lease: before use, it publishes a new lease, applies a
full fence and revalidates the status. It exposes no query/callback before this
check. Every required generation has a lease while its queries remain active. Cold opens remain under the database mutex until speculative open
safety is independently established. A snapshot with live queries never advances.
Incremental snapshot refresh is a separate pending step requiring unchanged
structural/reuse/reset/incarnation fences and validation after reading the tail. No callback or
query executes before acceptance. An ambiguous refresh discards the read-only
engine; it cannot checkpoint stale data.

A shared status page is adjacent to the database, independent of TMPDIR. Mapping
creation and retirement use the database mutex. It is ephemeral coordination, not
a durable commit record. All mutation publishers run under writer ownership;
publication uses aligned atomic fields and full fences. Each first connection
open establishes fresh state under the mutex; persisted control bytes alone never
establish authority after restart. Active mappings hold OS-backed participation
handles, so last-owner cleanup cannot unlink a live authority.

An inability to publish a mapped update must never silently select an unannounced
writer fallback. The prototype will publish a disable marker before falling back;
fast readers check it before and after admission. If that revocation cannot be
published, the writer must fail before modifying database bytes. Already accepted
readers retain ordinary OS-backed version leases. The marker is removed only when
no mapping participant survives, under the database mutex. This failure path and
its extra filesystem checks need measurement and forced-interleaving tests.

Required audit before enabling anything:

| Transition | Existing location | Required notification |
| --- | --- | --- |
| Commit publication | WalIndexService.ConfirmTransaction | publish visible version after verified durable commit |
| Allocate reclaimed WAL position | DiskService.AllocateLogPosition | reuse epoch before any overwrite |
| Rewrite earlier transaction IDs | DiskService.RewriteLogTransactionIDs | reuse epoch before prefix rewrite |
| Checkpoint lease scan, backfill, clearing | WalIndexService.TryCheckpointCore | structural begin before lease scan, end after writes |
| Full truncate/reset | WalIndexService.Clear / DiskService.SetLength | structural/reset invalidation before truncation |
| Failed append rollback/truncation | DiskService.WriteLogDisk catch | invalidate before truncating a potentially observed tail |
| Header journal recovery, format promotion, rebuild | engine open / disk header helpers | invalidate before any write; never publish quiet after uncertain failure |
| Retained writer state | close/handoff and next mutex acquisition | bounded, detached state; no stale writable streams or delayed checkpoint |

The writer-reopen candidate is separate: capture only a fully closed, quiescent
engine's replay metadata, validate it under the next writer mutex ownership, and
replay verified appended frames. It must restore transaction IDs, free positions,
header/pragmas, checksum sequence and retirement bookkeeping, not only query
indexes. Cache rejection uses a complete fresh open. Pending work includes an
independent shadow oracle and bounded cache ownership tests.

The initial idle-lease experiment failed 14 existing checks: it delayed last-reader
checkpoint cleanup and allowed a retained WAL to shadow a deliberately damaged
data page. No assertions were relaxed. Releasing the lease at the last active
query restored all 88 focused checks. The idle engine is now untrusted until
readmission, expires after 100 ms using a monotonic clock, and is discarded if
its page cache or opening WAL exceeds 4 MiB. Those component limits are not a
claim that total process RSS is 4 MiB.

`model-shared-admission.py` enumerates SC admission/checkpoint interleavings and
writer death at each modeled boundary. It detects omitted final validation,
unmarked destructive work, and ignoring leases. A separate prefix-refresh model
detects ignoring reuse epochs. This is a bounded abstraction; native forced
interleavings, OS lifetime tests, and architecture/runtime validation remain gates.
The native cache-admission negative control checkpoints between the initial
status read and lease publication: validation falls back successfully; bypassing
validation reads a reclaimed WAL address and fails the otherwise valid query.

Subsequent audit changes (pending final qualification): ordinary one-shot
connections no longer create control files; the second regular operation may
create them, and final-close cleanup never creates an otherwise absent authority.
Every participant still joins an existing authority before writing. Revocation
checks use native existence errors so missing markers do not allocate exceptions;
access/IO failures fail closed. Windows requires the parent's qualified shared
handle support so idle snapshots cannot prevent WAL deletion or rebuild rename.
The mapping owns and finalizes its acquired pointer reference, including partial
construction, independently of whether the connection was explicitly disposed.
Idle expiration performs no checkpoint or other durable work.
