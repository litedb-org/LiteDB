# Background automatic checkpoint for Direct cores

The owner approved Candidate C on 2026-10-27 for Phase 1: defer automatic
checkpoint to one process-owned worker, with bounded admission of successor
writers. This is an accepted behavior contract, not a claim of implementation,
safety qualification or measured performance. The barrier-reduction redesign is
separate: [litedb-org/LiteDB#3134](https://github.com/litedb-org/LiteDB/issues/3134).

Implementation is staged. The checkpoint/group-sync interlock is currently
**unactivated**; deterministic tests install it before concurrent operations.
Production keeps synchronous automatic checkpoint and the existing close paths.
The writer-admission and worker components are also unactivated prerequisites;
only deterministic xUnit fixtures install their nullable disk bindings. Core
lifetime integration remains to be built.
Do not enable the interlock in production before final-close group-I/O draining
is integrated: the current close contract can interrupt a pending group without
waiting for its native sync to return.

## Current implementation boundary

Slices 1–4 provide the accepted contract, nullable checkpoint/group interlock,
`CheckpointWriteAdmission` and `AutomaticCheckpointCoordinator`. No production
open installs these components or starts a thread. The nullable bindings are the
internal OFF switch; `BypassDirectHost` does not change them.

Writer intent reaches explicit/automatic transactions, FOR UPDATE, internal
rebuild/migration writers and first-write snapshot upgrades. CP0 writers retain
permit identities while unthrottled, so re-enabling can grandfather more than 64
without inventing a new count. WAL reservation, failed-append restoration,
retirement metadata and successful truncation publish logical tail under the
existing writer serialization. Successful header commits recompute pragma state;
repeated commits at the same N preserve hysteresis. Admission timeout does not
consume input or acquire a transaction ID/lease.

The worker owns only core services and one lazy thread. State-only tail and
last-version-unpin notifications cannot start I/O on their caller. `Request`
starts the thread outside storage locks; post-transaction automatic request
routing and recovered-tail publication belong to activation in slice 6. Request
consumption at pass start preserves a pending request racing completion; stale
service-incarnation requests are ignored. Component pause/close joins and the
explicit WAL executor join are tested independently of host shutdown. Terminal
publication wakes admission and worker waiters without joining under storage
locks.

Next is slice 5: Direct core/facade ownership, registry admission outside slow
joins, single-owner terminal cleanup, group-I/O drain, and explicit disposal error
surfacing. Public host checkpoint/disposal and replacement must acquire these
leases in the correct order before enabling the feature. In particular, do not
bind the barrier or worker in production while final close still interrupts a
paused group sync by disposing its handles. Component tests do not qualify that
host interaction. Later process-death, modeled-power-loss, compatibility and
performance slices remain required before activation/acceptance.

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
