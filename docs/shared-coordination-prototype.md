# Shared mapped reader admission

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
query checks a stable status, publishes its actual snapshot version through the
existing reader-slot file, executes a full memory fence, then checks storage
identity/structural/reuse/reset epochs again. No query or user callback executes
before that final check. A commit that races admission can leave the accepted
reader at its earlier protected version; a later query observes the new version.

The control page uses a 4 KiB read/write mapping beside the database, independent
of TMPDIR. Fields are aligned 64-bit values accessed with Interlocked operations,
including on 32-bit runtimes; accessor writes are not synchronization primitives.
The database mutex serializes publishers. Odd seqlock/structural values refuse
admission, and bounded retries fall back to mutex ownership. Creation and
retirement require that mutex. OS-backed participation handles prevent unlinking
an authority while any participant survives. An independently validated protected
open is required before each connection trusts a page. First participation retires
recognized stale control files after proving all old handles are gone; unknown
files are preserved. The page is never a durable commit record.

The initial protected open establishes the connection's slot file. Fast admission
reuses that live file; it does not create or clean registry directories outside
ownership. Concurrent count/slot writes retain the parser's fail-closed behavior.
A scan that preceded publication is detected by the final epoch check. A reader
that already passed admission keeps an OS-backed lease for every live generation;
no minimum-version approximation is introduced. Process death releases liveness.

An idle cache owns no lease. It expires after 100 ms using a monotonic clock and
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
| Destruction is announced before lease inspection | Shared writable opens and explicit rebuild; WalIndexService checkpoint structural scopes | Forced checkpoint between status read and lease publication; structural-marker negative control |
| Every physical prefix overwrite invalidates cache authority | DiskService.WriteLogPage immediately before write, including safepoint rewrites; legacy transaction-ID rewrites; failed-append truncation | SharedWalReusePublication tests; three-generation native reclaim/reuse tests |
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

The bounded SC model in `scripts/model-shared-admission.py` covers modeled
admission/checkpoint orders and interrupted publishers. Native controls detect
skipped final validation with a reclaimed-address read failure and ignored leases
with changed snapshot payloads. The structural control detects incorrect status
visibility. These finite tests do not establish arbitrary device or filesystem
fault behavior. The full platform, recovery and performance acceptance results
belong in [the results note](shared-performance-next.md); unresolved gates keep the
PR draft. Incremental writer replay is a separate, excluded experiment.
