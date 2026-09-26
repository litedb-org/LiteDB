# Detached Shared writer replay prototype

Isolated experiment; not yet qualified for the production PR. All concurrent
Shared participants must run the same build. There is no mixed-version gate.

A normal close first ends transactions and performs its existing close checkpoint.
Only then may it detach WAL indexes, observed transaction IDs, free-slot metadata,
logical/file headers and one validated confirmation frame. No old engine, stream,
page cache, transaction, lease or callback is retained. A fresh engine always
opens the files, validates the data header, salt, journal and retirement root,
and constructs new services. It imports detached metadata only under exclusive
writer ownership after matching storage identity/reset/structural/reuse fences,
exact file-header bytes, and the old confirmation payload, sequence, count and
digest. Full recovery handles every rejected candidate.

Writer opening has a separate admission generation: readers are blocked during
open and earlier cached read state is invalidated afterwards. A harmless open
can preserve the writer-prefix fence. Actual initialization, header-journal
recovery, upgrade/rebuild, format promotion, checkpoint, tail removal, safepoint
rewrite and reclaimed-slot write invalidate the storage fence before mutation.
An interrupted open invalidates authority identity and requires fresh recovery.

The retained prefix contains complete committed transactions, plus independently
validated retirement records. An abandoned/unconfirmed suffix rejects capture.
On reuse, the existing full-recovery loop validates only the appended suffix;
its observed transaction IDs, checksum sequence, confirmed end, free positions,
header and index updates use the same implementation as ordinary full recovery.
Changed/reused bytes anywhere before the cached end invalidate this shortcut:
nonshrinking WAL length alone is never sufficient.

Capture is bounded to 16 MiB of logical WAL and 8,192 entries in each retained
metadata category (and across index version entries). Capture copies live entries
into bounded collections so pruning or clearing a formerly large engine cannot
retain its old backing-array capacities. These bounds are not an RSS
claim; actual retained bytes and lifecycle CPU need measurement. Retirement
witnesses can encode many historic transactions per physical frame, so the entry
bound applies separately from the WAL byte bound. State owns no OS resources or
protection and cannot delay checkpoint/reclamation; it is dropped on next open,
rejection, or connection disposal. Cached corruption detection is bounded by the
same storage-fence assumptions as read caches; cold/full recovery remains the
independent complete WAL validator.

Pending gates include native append/checkpoint/reuse/reset/kill interleavings,
positive and deliberately broken fence controls, full-state/index/cold/raw-file
oracles, complete regression/fuzz coverage and paired production throughput,
latency, CPU, replay/capture costs and retention measurements. This note describes
the proposed mechanism, not a claim that those gates have passed.
