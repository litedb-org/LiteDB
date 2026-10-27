# Background automatic checkpoint for Direct cores

The owner approved Candidate C on 2026-10-27 for Phase 1: defer automatic
checkpoint to one process-owned worker, with bounded admission of successor
writers. This is an accepted behavior contract, not a claim of implementation,
safety qualification or measured performance. The barrier-reduction redesign is
separate: [litedb-org/LiteDB#3134](https://github.com/litedb-org/LiteDB/issues/3134).

Implementation is staged and **default OFF**. The internal
`EngineSettings.BackgroundCheckpoint` switch defaults to false, leaving all three
disk bindings null and retaining synchronous automatic checkpoint. An eligible
process-owned Direct core can opt in at open; tests can also enable that core via
the TESTING/DEBUG `InstallDirectCheckpointLifetime` fixture entry point before
concurrency starts. `BypassDirectHost` alone does not grant that authority.

## Current implementation boundary

Slices 1–5 provide the accepted contract, checkpoint/group interlock, bounded
writer admission, coalescing worker and composed `DirectCheckpointLifetime`.
The core owns the lifetime through the shared EngineState; facade disposal owns
only its transaction-monitor view and its host reference. Closing admission is
separate from Disposed so late worker/native-sync failures remain observable.

For an installed lifetime, final close cancels starts, drains captured/native
group I/O, then joins worker I/O before disposing transactions and services and
performing the enabled close checkpoint. It releases the group leader lock before
joining the worker. One cleanup owner retains the first causal error and attaches
secondary cleanup errors. Worker-owned terminal cleanup never self-joins;
external close waits for cleanup **and thread exit** before releasing native
admission. Non-worker terminal cleanup is queued after immediate stop publication
because callback failures can originate under storage locks. Explicit non-final
and final disposal surface the retained cause; repeated disposal is resource
idempotent and retains the error. Finalizer/reaper cleanup cannot throw.

Stop and the installed path's acknowledgement check share a terminal gate: a
completed writer delayed before acknowledgement cannot succeed after stop wins.
No commit durability barrier or persistent checkpoint ordering has changed.

Host release and maintenance publish admission under the registry gate and do
slow work under a per-host lifecycle gate outside it. Closing/maintaining entries
and native authority remain until cleanup/replacement finishes. An installed
closing host refuses attachment (and preserves a terminal cause); unrelated files
can open while it drains. The default synchronous path retains its existing
same-file wait-and-reopen behavior, reentrant-open refusal and pending-group close
expectations. Existing lifetime tests remain unchanged. Thus the paused-group
interaction is resolved for the installed owner without prematurely changing the
production close contract.

Writer intent, permit identities across CP0, logical-tail publication and reader
backoff remain as described below. State-only tail/unpin notifications cannot
start I/O on their caller. Public explicit checkpoint preserves the single-facade
restriction and joins a running pass before returning its own attempt's count.
A storage-lock reentrant close is refused before dropping the facade reference,
so an ordinary later close can retry.

Maintenance prerequisites from slice 6 are also present: rebuild pauses/joins
before transaction exclusion, aborts replacement on a failed worker join or close
checkpoint, and binds a fresh lifetime only after successful synchronous Open.
Old requests cannot start work on the replacement incarnation. A committed CP0
while a pass is started lets that pass finish and suppresses later close work.

**Slice 6 integration is present behind the default-off switch.** Successful Open
binds the coordinator, barrier and recovered-tail admission only after synchronous
recovery, migration and trimming finish. The Direct host supplies explicit core
authority; read-only, Shared/coordinated, bypass-only, volatile-WAL, memory/temp and
caller-stream engines cannot acquire it. A facade publishes the initial request
after Attach releases the registry gate, before returning to its caller. Thus an
already recovered high tail closes admission before any facade can write, without
starting a worker during recovery or under the registry gate. Compatible facades
share the same worker. Different internal switch settings require separate opens.

For an installed core, automatic transaction completion queues a request after
snapshots, group dependencies, transaction ownership and permits are released.
Rollback also notifies after release, including non-final facade owner cleanup:
safepoints and returned allocation pages can cross high water without a commit.
Strict threshold/equality, CP0 cancellation and
restart, large pragmas and synchronous volatile-WAL fallback retain their stated
semantics. Explicit checkpoint and close still complete synchronously. Successful
rebuild binds a fresh incarnation during Open and publishes its recovered backlog
after Open returns; retired requests cannot operate on replacement services.

**Next: slice 7**, the full forced scheduling, sustained-writer, reader/page-lease
and failure matrix. Slice 8 worker process-death/modeled-power-loss, compatibility
and performance qualification also remain required before production activation
or acceptance. Current slice 6 exception/forced-interleaving tests, including
file-image recovery tests, do not establish worker process-death or power loss.

## Accepted observable behavior

Once activated, a commit crossing the automatic threshold may return before
checkpoint. A later maintenance failure surfaces on subsequent operations,
attachment, explicit checkpoint and explicit disposal; it cannot retract an
already acknowledged transaction. WAL can retain more committed data before
reclamation. New writers can wait or time out under pressure. Explicit checkpoint
and final close inherit pending work. Cache-only commits still promise OS-cache
completion; deferral can increase exposure to power loss. Durable commits still
require their own covering WAL device/directory barrier before acknowledgement.

There is no layout/version change, forced rebuild, weaker WAL verification,
removed barrier or changed snapshot interpretation. Filename-backed writable
Direct process-owned cores, including encryption, are the only eligible hosts.
Shared (legacy and coordinated), read-only, memory/temp and caller streams keep
their synchronous policy. `BypassDirectHost` alone grants no worker authority.

## Trigger and bounded writer admission

`N = CHECKPOINT` remains logical 8,192-byte pages, with the strict trigger
`logical WAL bytes > (long)N * 8192`. Trailers, padding, encryption and header
journals do not count toward N. Arithmetic casts before multiplication, including
`int.MaxValue`. High water is `H = 4 * (long)N` pages: close fresh write admission
at `L >= H`, and reopen at `L <= N`. Partial reuse without reducing the logical
tail does not reopen admission.

Positive-N operation permits at most 64 write-capable transactions per core.
Acquire before any transaction lock, ID, snapshot or mutation; explicit
`BeginTrans` reserves from the start. Joining an owned transaction reuses its
permit. Propagate actual read/write intent, including FOR UPDATE, pragma/header,
collection/index and allocation operations. Check owner/facade/thread validity
before waiting and after waking. Unreserved upgrades and reentrant callbacks may
only acquire without waiting under their existing read/operation lease; refuse
before mutation when admission is unavailable. Pure readers remain possible.

Fresh writers wait outside storage locks on a monotonic effective TIMEOUT.
Timeout changes no storage and leaves the engine usable. Stop/close wakes waiters;
terminal failure wins over timeout. Never retry caller enumerables or an unknown
commit. Release permits after transaction, snapshots and group dependencies have
finished, including rollback, failed registration, abandonment and disposal.
Every WAL tail extension, including safepoints and rollback, closes admission
under writer serialization when required. Grant/close races count as admitted
writers. Admission never takes the writer while holding its state lock.

An admitted transaction can exceed H and finish or roll back without waiting on
itself. For the admitted set A, ordinary tail growth is bounded conditionally by
`H + sum(remaining appended frames for each member of A)`, plus maintenance
metadata. This is **not an absolute disk quota**: transactions have no fixed
maximum byte size. TransactionPageLimit bounds retained memory, not total WAL.
Report logical tail and physical WAL separately; include actual frame/padding,
journal, encryption and retirement bytes, without counting preallocated extents
as EOF or double-counting retirement metadata.

`CHECKPOINT=0` disables automatic/close checkpoint and this admission throttle;
explicit checkpoint still runs. Cancel queued work on a committed zero pragma,
but complete any started persistent pass. Positive changes recompute predicates
and wake waiters atomically. On zero-to-positive, grandfather all active writers,
even more than 64, until they drain. Permit identities survive disable/re-enable
without a late release decrementing a new incarnation. The volatile-WAL
CheckpointEachCommit override stays synchronous, including a stored zero pragma.

## Worker and lifetime requirements

One lazy background Thread belongs to the Direct core, shared by every facade.
It retains services, never facade lifetimes, pages, transactions or callbacks.
One running pass and one coalesced pending bit are permitted. Request epochs and
service incarnations prevent completion from erasing a racing request or using
replacement handles. Idle workers park. Preserve the 50 ms to 1 s reader
backoff; no progress must not produce a scan/sync busy loop. Last relevant unpin
wakes maintenance without doing I/O on the reader's disposing thread.

Worker states are Idle, Queued, Running, Paused, Closing, Faulted and Stopped.
Only the last maintenance lease resumes starts. Closing/failure is irreversible;
failure wins over successful completion, resume and notification. Thread-start
failure also latches terminal state. No worker runs during cold recovery,
conversion or partially initialized open. Publish recovered tail/admission state
before permitting writes after successful open.

Explicit checkpoint preserves Direct's single-facade restriction, refuses a
reentrant storage-lock self-wait, pauses new worker starts and joins a current
pass. Then it performs its own unrationed synchronous attempt and returns that
attempt's page count, possibly zero. Live readers retain today's bounded
partial/no-progress semantics. Completion is not a promise to evict snapshots.

Non-final facade disposal cleans its owner without cancelling peer maintenance.
Final close refuses new attachment/operations, wakes admission, cancels queued
starts and joins running I/O before disposing services/native admission. Drain
owners under existing thread rules, then perform the existing close checkpoint
when enabled. Pinned generations remain recoverable in WAL. A stalled kernel
sync cannot be cancelled by closing its handle or reported as a successful drain.

Maintenance/rebuild pauses and quiesces before storage exclusion or replacement,
retains path/native authority, and rebinds a fresh incarnation only after a
successful open. Failed drain aborts replacement. Keep closing/maintaining
registry entries while releasing the static host gate around slow work. Never
join under that gate, coordinator/header/index/writer locks or the group leader
lock. Terminal cleanup has one owner: a worker cannot join itself; external close
waits for its cleanup/exit. Do not set Disposed early enough to hide a late I/O
failure. Explicit disposal surfaces the original causal error with cleanup errors
attached; repeated cleanup is idempotent and finalizers do not throw.

## Executor, lock graph and persistent order

The disk-owned CheckpointBarrier lease serializes checkpoint with native group
sync, including target/handle capture and durable-prefix publication:

- Group: leader `_sync` -> barrier -> WAL writer; release writer for native sync,
  reacquire to publish only the captured prefix, then release barrier and leader.
- Checkpoint: transaction exclusion -> commit/header -> barrier -> index -> WAL
  writer, retaining the barrier across the existing checkpoint protocol.
- Dependency/snapshot capture: index -> WAL writer, without the barrier.
- Ordinary/fallback commit: header -> writer. Fallback/callback paths can call
  EnsureDurable under header; the group path never acquires header/index under
  the barrier. Checkpoint never enters the group leader lock under the barrier.

All PendingVersions and CommitReadVersionLimit remain live generations when
checkpoint wins before group publication. Checkpoint cannot acknowledge a group
ticket, reclaim its generation or borrow a commit sync as a checkpoint barrier.
Publish terminal failure before writer/barrier release; queued participants must
not start new I/O or acknowledge after stop. Cleanup follows lock unwinding.

Preserve the existing ordered protocol in
[page and WAL checksums](page-and-wal-checksums.md): full-WAL validation before
mutation; WAL redo/body sync; bound old-header journal sync and required directory
publication; safe data backfill and data sync; only on full reclamation, durable
salt/root header publication before WAL truncation and index/cache reset. Keep
partial-retirement witnesses, slot clearing and all encrypted fallbacks intact.
Queued work has no persistent authority; cold recovery uses verified data/WAL.

## Qualification before activation and acceptance

Force both barrier orders and failures, callback/header fallbacks, zero-frame
dependencies, pending generations, all-facade failure, admission grant/close and
timeout races, oversize safepoint/rollback, pinned readers/page leases, pragma
transitions, queued/syncing close and explicit checkpoint, finalizer lifetime,
replacement/recovery and repeated interruption. Verify full payloads, indexes,
allocation ownership and an unrelated sentinel. Register hooks/reachability;
mark xUnit-only scenarios advisory with the smoke reachability gap recorded.
Exception/interleaving tests do not establish modeled power-loss or process-death
coverage. Those campaigns, old-v5 refusal on stable aligned copies, dirty-WAL
compatibility and full runtime/platform CI remain required before activation.

Benchmark only on hosted runners with production Release binaries. Preserve the
5,000 cache-only auto-commit foreground <=1.10x v5 gate, and require the matched
contiguous insert-through-immediate-close/drain <=1.10x comparison for acceptance.
Report foreground, remaining drain, exact peak physical WAL/logical pages,
admission waits, commit p50/p95/p99/max, sustained cycles and pinned-reader
controls. Keep CP0 controls, durable/group scaling, bulk/query regressions and
<=1.05x point lookups. Use same-job interleaved pinned binaries and repeated noisy
macOS runs. A foreground-only pass establishes latency relocation; it cannot
establish the end-to-end gate. This document contains no Candidate C measurement.
