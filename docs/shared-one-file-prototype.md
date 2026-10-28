# One-file Shared prototype integration contract

ABI 2 is opt-in and dormant by default until slices 7–10 are qualified. The
existing multi-file Shared implementation remains the default. ABI 1/2 deployment
requires all old executables stopped: unmodified old clients cannot be fenced by
a protocol they do not implement. No database-format change or rebuild is needed.

Both modes use NativeAdmission and DatabaseFileLock.Admission on the actual data
file: writable Direct exclusive, Shared shared, for the full physical participant
lifetime. Unix read-only Direct uses the marker/admission handshake described in
[native admission](direct-process-host.md#native-admission); both Shared families
publish admission before checking that marker, using disjoint family bytes as
specified in [ownership transitions](shared-one-file-ownership.md).
The path, physical identity, sidecar namespace and native-volume policies are the
Direct base's policies. Arbitrary streams, memory and temp are not pooled.

Order: path creation/replacement rendezvous -> process-entry publication gate
(short; no slow initialization under it) -> native admission -> Shared turnstile
-> main mutex -> WRITE range -> core/storage locks. Snapshot/reader gates must
never be held while waiting for writer ownership. Operations must not reenter the
registry gate. Teardown finishes cores/results, then unmaps and closes slot/DMS,
then releases data admission. A result retains actual mapping/admission resources.
Direct's existing global cold-open gates remain a separately reported limitation;
this prototype adds no work on Direct operations.

Direct and legacy Shared facades carry a reference-identity TransactionOwnerToken,
propagated through EngineSettings to every reopened core and monitor. Mapped Shared
facades share their physical participant's token and one generation-tagged local
transaction lease. That lease records the actual Thread and facade: same-thread
foreign facades refuse, other threads wait, and implicit abort/retry reconciles
ownership before completion. Tokens contain no Thread, AsyncLocal or execution
context. Legacy calls still use actual Thread identity for lookup/completion;
this does not authorize moving an explicit transaction across threads.

SharedMemoryLayout fixes the bootstrap and lock offsets. Configurable block stride,
capacities and map size are declared in two checksummed envelopes. Locks are beyond
the maximum payload at 1 GiB so Windows range locks never intersect mapped data.
Process requests and wake words have separate cache lines. A reserved 512-byte
coordinator region has no semantics yet; no coordinator is implemented. A newcomer
must eventually claim/publish/wake without the writer mutex (slice 8); slot
publication therefore needs independent bootstrap serialization, not writer tenure.

Through slice 3 all read-only engines still open under the named mutex; ABI-1
cached admission is bypassed. Exact mapped records replace only the reader
provider. Unknown liveness/records skip checkpoint and reclamation. Generation
exhaustion throws before query/callback execution. Rebuild, file-replacing upgrade
and auto-rebuild are explicitly refused until slice 9. Existing artifacts are
preserved/refused; offline cleanup is not implemented in this round.

The first exclusive-DMS initializer owns WRITE while invoking protected storage
validation. No live ABI-2 writer can exist in that state. Live attachment releases
the independent bootstrap gate after publishing its slot/request and before
waiting for main ownership. Slice 5 now pairs every writable core open, including
getters/BeginTrans and live participant validation, with main + WRITE. Bootstrap
validation borrows the initializer's existing WRITE tenure. Read-only snapshots
keep their nonmutating route.

Named-mutex abandonment can precede the kernel closing a killed process's native
lock descriptions. A successor holding abandoned main ownership retries WRITE
for at most five seconds, requiring an actual native acquisition before storage
access. Abandonment never permits bypassing a live WRITE lock. Other ownership
mismatches still refuse immediately. The two-process ownership tests and the live
native-writer negative control cover both outcomes.

Exclusive DMS and WRITE permit reusing a matching quiescent envelope only after
protected DB/WAL validation. Checksum/identity or supported layout/capacity/size
mismatches reset the ephemeral authority; an untrusted namespace, unknown ABI or
inconsistent declared physical length refuses without changing it. Live participants
retain their declared layout: new attachments use a free existing slot or refuse
capacity exhaustion, never resize. Only quiescence permits adopting new capacities.
Reuse-validation death tests assert the reuse counter and reachability markers,
repeat interruptions inside actual startup repair and around Ready/final close,
then verify the same authority and indexed committed data after recovery.

Through slice 4 the registry shares one mapping, DMS, process-slot lock, exact
reader table, durability diagnostics and checkpoint backoff across compatible
local facades. Main mutex/core state remain per facade; mapped local transaction admission is shared in slice 7.
The participant captures no facade callbacks or facade token. Initialization is
per physical identity; unrelated identities never wait behind its entry gate.
Closing the last facade/result retires that exact entry without erasing a successor.

Qualification is currently Linux x64/.NET 8+ on the mapped local-volume allowlist.
Other mapped platforms explicitly refuse opt-in; their prototype tests report a
capability skip, while native range tests remain enabled. Windows/Darwin/ARM and
Framework mapped qualification are outstanding. Defaults retain ABI 1. The map
starts with committed-version unknown and fast-admission disabled. Slice 5
publishes writer/recovery/structural/reuse/reset identities. Slice 6 enables mapped warm
admission only after protected validation and a covering durable sync.

Recovered confirmations can survive only in a dead writer's OS cache. A writable
protected open verifies/replays storage, syncs the recovered WAL and its directory,
and only then publishes a quiet committed version and repairs abandoned structural
fences. A failed or unverifiable recovery barrier leaves the authority unknown.
Grouped commits publish no further than their covered prefix; cache-only/degraded
commits retain recovery fencing instead of advertising a durable version. A
terminal engine cannot publish successful close or clear a structural fence.
Every publication validates the live Ready/envelopes. Failure cleanup releases
WRITE even when a checked epoch or publication fails. Each core captures its
writer tenure so delayed stop cleanup cannot fence a successor.

`SharedOneFilePublicationReview_Tests` retains the adversarial regressions.
`SharedOneFileRecoveryCuts_Tests`, `SharedOneFileDurablePrefix_Tests`,
`SharedOneFileCheckpointFences_Tests`, `SharedOneFileWriteFailures_Tests` and
`SharedOneFileOwnershipFences_Tests` cover internal cuts, prefix ordering, stopped
unwinding, exception-safe release and stale cleanup. The mapped allocation and
opening-mutation classes add full payload/index and independent page-graph checks.
These exception/process-death/interleaving tests do not qualify real power loss,
other architectures or core handoff.

The Phase 2 port keeps Phase 0's retry ownership reconciliation, live WAL-restored
timeout access, callback execution frames and close-before-unlock rules. It builds
on Phase 1's process-owned Direct host and group commit without changing their
operation paths. `SharedOneFilePhase1_Tests` exercises mapped retry/failed-Begin
cleanup, pinned and unpinned callback isolation, and protected timeout restoration.
Existing default-mode callback, cursor ownership and commit-failure tests continue
to apply; custom streams cannot select the mapped physical-file protocol.

Slice 6 shares one snapshot cache, inactivity timer and read pacing budget per
participant. Two protected read-only opens establish cache demand. The idle cache
owns no reader record; active results retain the exact generation, mapping and
data admission. A facade close cannot retire a peer's cache. Final participant
release closes idle cached storage before unmapping and makes local reattach wait
for completed native teardown. Cached settings borrow participant resources and
delegates rather than retaining a creating facade. Spilling sorts, page caches
above 4 MiB and WALs above 4 MiB cannot remain cached; the inactivity timeout stays
100 ms. Expiry never invalidates an active result. Stateful transforms retain their
single-execution protected route and capacity fails before callback execution.

Warm admission atomically samples the sequence, Ready/disable/recovery state,
committed version, structural/reuse/reset epochs and writer tenure, and validates
both immutable envelopes against the attached authority. Each sample rechecks its
sequence. The cache must match the sampled version and storage tenure. Admission
publishes the packed exact-generation record, executes a full memory barrier,
then repeats the complete sample and requires the *same sequence and version*.
An odd or changed fence, damaged envelope, recovery owner, unknown version or
disabled state falls back before any query or callback executes. Writers fence
before advancing tenure or changing storage. Stopped/uncertain writers disable
fast admission until independent protected validation and sync repair the fence.
The mapping remains reconstructible metadata, never durable evidence itself.

All warm admission checks and reader publication/release use mapped atomic words
and process-local gates. No pathname, liveness, file, named mutex or native range
lock probe occurs on admission. An internal test-only counter at coordination
I/O/lock seams asserts zero attempts for repeated buffered warm reads from another
facade while main ownership is held elsewhere, with the slow path as its positive
control. Query page misses may perform ordinary storage I/O. Last streamed-result
cleanup retains the existing best-effort checkpoint route after its lease ends;
it is not part of admission. Advisory writer pressure uses expiring mapped request
words and the existing 7:1 work-based pacer, outside the cache gate and reader lease.
Stale hints never evict a generation or authorize reclamation.

The SharedOneFileWarm* classes force both status samples' load boundaries, a
remote writer's scan on either side of record publication, every writer/open/commit
fence death with a surviving warm result, and reader death. Repeated recovery checks
complete payloads, secondary indexes and unrelated records. The reclaimed-WAL
negative control must fail when the final check is bypassed. The bounded SC model
also rejects unmarked destruction, ignored records and premature version visibility.
These are Linux x64 exception/process-death/interleaving tests; they do not qualify
ARM memory ordering, actual device power loss, unmodified old clients or default
enablement. SharedOneFile remains false by default.

Escaping engine-created Shared results own a detached cleanup record. Its GC handle
roots the inner cursor, pages and release callback without rooting the escaping
wrapper. Explicit disposal releases that record immediately. A collected wrapper
publishes its record to a lock-free pending list; a normal-thread reaper disposes
the inner cursor before releasing the generation, local reader and participant.
The finalizer performs no I/O, user callback, monitor acquisition or wait. Cleanup
can wait for a live cache operation without preventing other finalizers from
running. The handle is freed after both disposal and the release callback have
been attempted, including exceptional cleanup. Reclamation is eventual, so
`GC.WaitForPendingFinalizers` alone does not wait for deferred disposal.

Cache admission takes cache Gate before the reader-table gate. `Unregister` must
release the table gate before calling `Release`, including its reentrant caller
scope: final `Release` can dispose the cache. Once final release marks the table
closing with zero references, another attach waits for close and retries a new
participant; it cannot register against the mapping being unmapped. The forced
schedule in `SharedOneFileWarmGenerations_Tests` holds cache Gate while the final
unregister starts, then verifies the table gate remains obtainable.

`SharedCoordinationDiagnostics.AdmissionOperations` is an admission coordination
counter for selected file/range-lock/owner/probe seams on the calling thread. It
excludes local monitor contention, deliberate Sleep/Yield pacing, direct native
open/turnstile seams, storage I/O and helper-thread work. A zero value establishes
that those selected seams were avoided; it does not establish zero syscalls for
a whole query. Cold and contended controls are in `SharedOneFileWarmCounter_Tests`.
The mapped platform guard remains Linux x64/.NET 8+ pending wider qualification.

The [ownership transition protocol](shared-one-file-ownership.md) specifies local
reservation/publication, native tenure, terminal cleanup, abandoned-owner handoff
and deferred disposal, together with their discriminating safety tests and limits.
