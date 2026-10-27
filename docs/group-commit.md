# Commit grouping

Plain file-backed Direct engines on .NET 8 and later group durable commits.
Encrypted, caller-stream, Shared and netstandard engines retain serialized
commit flushes. `DurableCommits=false` retains OS-cache acknowledgement.
No file format, recovery rule or public setting changes.

The header monitor orders transaction append and WAL-index insertion. Once its
frames have been appended and indexed, a transaction releases its snapshots and
collection locks, while retaining transaction admission until its durability
wait finishes. Another writer of the same collection can then use the pending
index. Readers pin at the last successfully synced version; publishing pending
entries does not expose them to readers. Collection-map callbacks retain their
existing publication lock through sync and confirmation.

Each appended confirmation receives an increasing in-memory ticket. A committer
also captures a dependency ticket when pinning a write snapshot. Capture holds
the index lock and then the WAL writer monitor, so the ticket covers every
pending version that snapshot can observe (and may conservatively include a
later append). Transaction completion waits for this ticket even when it writes
no confirmation frame. A new confirmation advances the same ticket. Zero is
safe only when snapshot capture established that no grouped append existed;
absence of dirty pages alone says nothing about durability.
Capture consults only an existing group; it never opens the lazy WAL writer
before registering the snapshot pin. Every published grouped version already
has its group, created before append and WAL-index confirmation.

A committer
enters the sync monitor and immediately takes the writer monitor, flushes managed
buffers and compatibility padding, and captures the latest ticket and version.
It releases the writer monitor and syncs the file descriptor. Appends can proceed
while that device operation runs. A successful sync releases exactly the captured
prefix; later appends wait for the next sync even if the device also persisted
them. Waiters whose tickets are already covered return without another sync.
A lone writer starts its sync immediately; there is no timer or collection delay.

Automatic write results, including no-op counts and already-existing indexes,
return only after their dependency is covered. Recoverable write errors also
cover it before rollback returns the original error: a duplicate-key error can
otherwise disclose an insert that is still pending. A failed dependency barrier
instead returns the I/O failure and stops the engine. Standalone `FOR UPDATE`
queries cover their dependency before yielding, because they release rather than
commit their query transaction. Ordinary read snapshots, including read-only
explicit transactions, select the durable prefix. Results inside an explicit
write transaction remain provisional until its `Commit`; promotion from a read
snapshot captures the write dependency, including for a subsequent no-op.

The sync uses the existing native error-reporting `fsync` / `F_FULLFSYNC` policy
(Windows: descriptor-based `FlushFileBuffers` through `RandomAccess`). It never
uses a buffered FileStream method concurrently with appends. Even the
`SafeFileHandle` getter flushes managed buffers, so the leader captures that
handle under the writer monitor before allowing appends. The descriptor-only
sync test verifies that later buffered bytes remain owned by their writer.
The WAL directory
barrier still precedes acknowledgement. Unsupported-sync handling retains the
existing documented degraded mode. Any actual failure publishes the engine's
fatal state before releasing the sync monitor, fails all pending waiters, and
tears down outside the monitors. Failure while flushing managed buffers or
capturing the descriptor publishes the stop under the WAL writer monitor, before
another writer can append. A leader rechecks terminal state after target capture
and before starting its device barrier; an already in-flight sync may finish but
cannot publish durability after another writer stopped the engine. The first
group failure records its causal WAL write diagnostic before stopping.
Reopening retains the existing unknown-outcome
contract for an interrupted or failed commit.

A partial checkpoint retains both the current reader limit and **every pending
commit version** as possible future reader versions. A barrier may expose an
intermediate prefix while later versions remain pending; retaining only the old
reader limit and newest version would reclaim that intermediate view too soon.
The checkpoint's WAL/data/witness/journal barriers remain in their original order.
Full checkpoint and generation reset require transaction exclusion, which pending
committers retain. Tickets can therefore remain monotonic across generation resets.

`GroupCommit_Tests.Concurrent_commits_share_barriers_and_preserve_durable_visibility`
forces four/eight committers on one/multiple collections, explicit and automatic
transactions, two held barriers, a partial checkpoint, and failure of either
barrier. It checks exact reader views and acknowledgements at each boundary,
two commit syncs for the whole group, no subsequent sync after a failure, and
file-image recovery with indexed results followed by a checkpoint and second open.
Its power-loss model retains complete images from successful barriers.
`GroupCommitRecovery` additionally tears, loses and truncates each later frame,
including images where later confirmations persist. The acknowledged prefix must
survive, all recovered documents must remain complete and indexed, read-only
recovery must preserve both sources, and writable recovery must repeat. Existing
WAL/checkpoint tests cover further torn/reordered/lost-write combinations. None
is a physical-device power-loss test.

`GroupCommitReview_Tests` retains the reviewer's failed-barrier reproductions for
EnsureIndex and automatic/explicit no-op deletes. `GroupCommitDependency_Tests`
adds successful and failed dependency barriers for Update/UpdateMany/DeleteMany,
DropIndex/EnsureIndex, Upsert returning false, unique/index errors, standalone
FOR UPDATE, and explicit read-to-write promotion. Saved successful barrier images
and the pre-append durable image model loss of unsynced writes, with exact
primary/secondary results across recovery, checkpoint and a second reopen.

Checkpoint preflight on an ordinary FileStream hashes the same bytes whose WAL
proofs and complete transaction set it validates. That checksum is consumed by
the header journal only while header/index/writer exclusion remains held, no
retirement records have been appended, and materializing padding changes no bytes.
Encrypted, caller-stream, unaligned and nonzero-padding cases retain the original
verification path. `CheckpointVerification_Tests` covers plain-file preflight,
padding fallback, corruption refusal without source mutation, indexes and reopen.

On 64-bit Linux, ordinary file WAL streams also reserve extents in 4 MiB chunks
with `fallocate(KEEP_SIZE)`. This is an optional allocation hint: it never changes
EOF, frame addresses, padding or existing bytes. Unsupported/failed reservations
disable the hint; subsequent ordinary writes retain their normal error handling.
Truncation resets the capacity hint. Journal retirement may release unused
reserved extents; correctness never depends on the hint remaining accurate.
The reservation can retain up to a chunk of unused disk allocation. Compatibility
padding still extends the logical file at flush boundaries: replacing that with
whole zero frames would change recovery semantics and is deliberately avoided.
`WalFileAllocation_Tests` checks unchanged EOF/payload, truncate/reserve/rewrite,
reopen and caller-stream behavior; existing WAL fault tests cover actual writes.

`GroupCommitStop_Tests` forces uncertain follower confirmation failures while a
leader has captured its target, both before and during its device barrier. The
leader and a zero-frame dependent committer must fail before teardown; no further
write or sync may start. Separate I/O and non-I/O managed flush failures verify
stop publication under the writer monitor. Repeated file-backed recovery checks
complete old/new unknown outcomes, seed payloads and exact secondary indexes.
These are exception/interleaving probes, not physical torn-write or device-reset
models; the existing WAL and allocation failure tests retain those other models.
