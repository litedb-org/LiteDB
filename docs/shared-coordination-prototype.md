# Shared mapped reader admission

> This restores the candidate archived at `130b339523cbe61efcef52b5d67252aee5a6cb53`,
> which #3014 excluded because of measured writer regressions, with cheaper
> publication, narrower mutation scopes, and bounded reader pacing. The
> [controlled experiments](https://github.com/litedb-org/LiteDB-Artifacts/tree/cad4d5fc381ad8deffdc3d32b54513de5156b64e/investigations/shared-mmap-writer-cost-2026-09-27)
> retain the measured tradeoffs and rejected alternatives. Historical results remain
> in [the archived report](shared-mapped-admission-results.md). Same-version
> concurrency remains the contract.

PR #3014's candidate assumes every concurrent Shared participant uses exactly the
same LiteDB version. Concurrent Direct/Shared, mixed-version and cross-machine
access are outside this protocol. The existing [connection identity contract](shared-mode-safety.md#connection-identity-and-lifetime)
also requires one absolute path and mutex naming strategy: concurrent symbolic-link,
hard-link or other physical-file aliases are unsupported. Path normalization does
not create an independent authority for a supported participant. Database and WAL
formats are unchanged.

## Admission and lifetime

Cold snapshots still open under the existing named database mutex. The first
protected read skips control-page probing and attachment. After two
consecutive read-only opens, a connection may retain a read-only snapshot. A warm
query reads a provisional status hint, publishes its actual snapshot version through the
existing reader-slot file, executes a full memory fence, then checks storage
identity/structural/reuse/reset epochs again. No query or user callback executes
before that final check, including both authoritative filesystem revocation probes.
The hint alone omits revocation probes and cannot authorize access. A commit that races admission can leave the accepted
reader at its earlier protected version; a later query observes the new version.

The control page uses a 4 KiB read/write mapping beside the database, independent
of TMPDIR. Aligned 64-bit fields use acquire loads on x64/ARM64, conservative
Interlocked reads on x86, and Interlocked stores. Accessor writes are not
synchronization primitives. Seqlock validation and the post-lease admission step
retain full memory barriers. A monotonic append commit updates only the version;
resets and interrupted publications retain structural sequencing.
The database mutex serializes publishers. Odd seqlock/structural values refuse
admission, and bounded retries fall back to mutex ownership. Creation and
retirement require that mutex. OS-backed participation handles prevent unlinking
an authority while any participant survives. An independently validated protected
open is required before each connection trusts a page. First participation retires
recognized stale control files after proving all old handles are gone; unknown
files are preserved. The page is never a durable commit record.
New participation, status and revocation files are fully written and flushed in a unique temporary
file, then renamed in the same directory without replacing an existing destination.
Process death before publication can leave an ignored `.litedb-control-*` temporary
file (at most 4 KiB per interrupted creation), but cannot publish a partial authority.
Power-loss durability of directory entries remains filesystem-dependent; missing
or unrecognized control files never authorize a snapshot.

Before waiting for ownership, a writer can publish a short scheduling hint in a
separate atomic word. A deadline, request sequence and active bit distinguish
requests made within the same millisecond. A completed writable engine clears its
own request with compare-exchange; it cannot clear a newer request. An abandoned
request expires after 100 ms without cleanup. The hint is advisory, not a count of
all waiting writers or an authority to read, reclaim or acknowledge storage.

While that hint is active, a connection charges a bounded local budget for cached
query execution. Requested pauses are at most 10 ms, outside the snapshot gate
and before lease publication; every admission check runs afterwards. OS delays
may exceed the requested time and are credited against the budget. Debt and
oversleep credit are bounded to 50 ms. Streaming execution is measured inside
Read/Dispose, excluding caller time between rows. Accepted readers retain their
leases normally. Inactive pressure clears the budget. The pacer charges seven
milliseconds of delay budget per millisecond of measured
work; it is a scheduling tradeoff, not a hard CPU quota or writer latency guarantee.
Disposal can finish during a pre-admission pause; the resumed call must fail its
ordinary disposed check without creating a lease.

The initial protected open establishes the connection's slot file. Fast admission
reuses that live file; it does not create or clean registry directories outside
ownership. Concurrent count/slot writes retain the parser's fail-closed behavior.
A scan that preceded publication is detected by the final epoch check. A reader
that already passed admission keeps an OS-backed lease for every live generation;
no minimum-version approximation is introduced. Process death releases liveness.

An idle cache owns no lease. The existing timer checks the monotonic last-use
timestamp and rearms itself, including when it visits an active streaming reader.
Reader completion updates that timestamp without changing the timer. The cache
expires after 100 ms of inactivity and
is discarded when its page cache or opening WAL exceeds 4 MiB, or when a query
spills a sort to temporary disk. Spilled engines close after their last active
reader, releasing the spill file while the connection remains alive. Those component
limits are not a total-RSS bound. Writers retire idle read state before taking the
operation-state lock, allowing Windows reader handles to be reused. A single read
between writes does not retain a snapshot. Streaming queries keep their leases
until disposal, even after a newer snapshot replaces the cache. Idle expiry only
closes a read-only engine; ordinary close/checkpoint rules retain durability.
Closing a coordination view releases its native handle before disposing the
accessor, avoiding an explicit flush of ephemeral control bytes (including from
the finalizer). Live peers retain their own coherent views. The page never supplies
durability evidence and is never trusted on startup without a protected open.

## Mutation and fallback

| Invariant | Responsible path | Discriminating evidence |
| --- | --- | --- |
| Destruction is announced before lease inspection | Actual startup mutations, automatic/explicit rebuild; WalIndexService checkpoint structural scopes | Forced checkpoint between status read and lease publication; structural-marker negative control |
| Every reuse batch invalidates cache authority before its first overwrite | DiskService.WriteLogPage before write, including safepoint rewrites, legacy transaction-ID rewrites and failed-append truncation; coalescing requires mutex exclusion of cold snapshot installation for the whole batch | SharedWalReusePublication and SharedWalBatchPublication tests; three-generation native reclaim/reuse tests |
| Published commits name durable visible state | Existing ConfirmTransaction signal after the established commit barrier | Document/index oracles and native overlapping commits |
| Failed mapping cannot create an invisible writer | RevokeIfPresent before fallback, checked before and after admission; revocation failure prevents the write | Unknown/unavailable control tests and active-reader fallback tests |
| A blocked rebuild open creates no coordination files | RebuildRecovery guard before authority setup | Existing exhaustive rebuild install/rollback matrix |
| Idle/failed/disposed state cannot own leaked resources | Lease release at last active reader, bounded timer, acquired-pointer finalization | Idle lease/WAL immutability, disposal, GC and native death tests |

The fast path is limited to NET8-or-newer x86, x64 and ARM64 runtimes and recognized fixed local
filesystems (ext2/3/4, XFS, Btrfs, NTFS, ReFS, APFS). Windows additionally requires
the parent's qualified POSIX-delete shared handles. Other storage, custom streams,
read transformations, or incompatible control paths use the existing path. Name
limits are checked conservatively on every participating target. Native marker
probes distinguish absence from access/IO errors without allocating missing-file
exceptions. NETSTANDARD participants revoke an existing authority before writes.
An unqualified reader can still open a protected read-only snapshot without
revoking peers. If that connection later writes, revocation must succeed before
the writable open; otherwise the write fails. Architecture-fallback tests verify
post-acknowledgement visibility and preservation of an already accepted reader.
Already accepted readers retain their leases through fallback.

The separate coordinator continues to publish every physical reuse: it does not
exclude cold snapshot installation for a complete WAL write batch. Only the Shared
participant implements the batching contract. Every new batch, including each
safepoint, must announce independently. Ordinary valid writable opens reconstruct
local state without invalidating cached storage. Initialization, journal repair,
checksum conversion, data/WAL tail truncation, and v7 upgrade have explicit
scopes; automatic rebuild starts its scope before inspecting external leases.
An odd sequence or structural marker from an interrupted publisher still requires
a protected open, ending its structural scope and establishing trust together.
Opening-mutation tests observe the fence at actual stream writes and truncations;
native recovery tests interrupt a writer inside a real protected tail repair.

The bounded SC model in `scripts/model-shared-admission.py` covers modeled
admission/checkpoint orders and interrupted publishers. Native controls detect
skipped final validation with a reclaimed-address read failure and ignored leases
with changed snapshot payloads. The structural control detects incorrect status
visibility. These finite tests do not establish arbitrary device or filesystem
fault behavior. The linked experiment evidence records tested revisions, native
architectures, fault models, and performance limits; throughput results alone do
not establish database safety. Incremental writer replay is a separate, excluded
experiment.
