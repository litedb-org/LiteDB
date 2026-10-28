# Direct process host

File-backed Direct connections share a process-owned storage engine.
Transactions remain synchronous and thread-bound; this does not introduce
thread-independent transaction handles.

One physical file has one live storage engine per loaded LiteDB assembly.
Opening a compatible file-backed `LiteDatabase` or `LiteEngine` attaches an
engine facade: it references the same storage services and has a fixed owner
view of the transaction monitor. The registry strongly retains each published
host by its native admission entry until final release, with canonical data/WAL paths
and a facade reference count. This keeps pending facade finalizers from
allowing a second core while the old streams remain open. Finalizers enqueue
release to a normal-thread reaper; they never wait for storage teardown.
A cold-path registry gate publishes closing/maintenance and reference changes.
Per-host serialization runs owner cleanup, final close and maintenance outside
that gate, so one file's slow teardown does not block unrelated files. Same-file
openers wait for final close outside the registry gate; recursive opening from
that closing thread refuses. The entry and native admission survive until
engine disposal finishes. New-host construction still uses the registry gate.
The native entry is stable while rebuild replaces its locked physical file.
Attach therefore still finds the maintaining host after the replacement inode
is published; there is no separate host identity rekey. Native admission keeps
the physical identity and canonical path checks, rejects hard links and binds
symlink aliases to the same entry. Other threads serialize through the native
gate across its identity swap; all attaches refuse until host maintenance ends.
Custom data/log/temp streams, memory/temp databases and the experimental
coordinator's internally owned engines retain their previous path.

## Transactions

Transactions remain synchronous and thread-bound. A transaction also has the
identity of its facade's monitor. An operation through another facade on that
same thread throws `InvalidOperationException` before joining or changing the
transaction. That facade's Commit/Rollback return false. Complete the owning
transaction before using the other facade on the same thread. Separate threads
can run independent transactions through different facades using the existing
collection locks. This deliberately rejects same-thread reentrant foreign work
instead of allowing thread-recursive collection locks to admit two writers.

Disposing a non-last facade rolls back its own idle transaction on the current thread;
it does not dispose a peer's transaction. Last dispose uses the engine-close transaction cleanup. Disposing an idle write transaction
from another thread stops the host and throws rather than leaving a
thread-owned write lock on a still-running host. Operations concurrent with
disposal of their own facade remain unsupported, by the connection lifetime contract.
There are no operation or cursor host leases and no per-call context binding.

## Settings and maintenance

Attach validates password, read-only mode, durability, compact storage, legacy
scan mode, effective cache/page limits, initial size, migration limit, requested
collation, upgrade/rebuild flags, invalid-time policy/timezone and read transform.
A null collation makes no new request; an explicit collation must match the live
header. Mapper state remains facade-local. Engine settings are captured on open;
pragmas are host-wide, as they were storage-wide in dev.

Manual checkpoint and rebuild require exactly one facade. Automatic checkpoints
still use the unchanged storage transaction gate. Replacement locks the candidate
before publication, retains the original lock across renames, then transfers the
registry to the live physical identity. Existing rebuild recovery markers and
rollback protocols remain authoritative. Missing-file openers take a path
bootstrap mutex before creation. Existing-file openers lock the physical file
without creating it, and recheck the recovery marker before opening the core.
Neither path can create a database in a replacement's missing-name interval.

## Native admission

The native lock implementation uses Windows LockFileEx and Linux
F_OFD_SETLK/F_OFD_GETLK (37/36) on the data inode. Darwin uses its own
F_OFD_SETLK/F_OFD_GETLK (90/92), including the arm64 variadic ABI, on a separate
lock inode. These commands are present in Apple's
[fcntl.h](https://github.com/apple-oss-distributions/xnu/blob/main/bsd/sys/fcntl.h)
and handled by
[kern_descrip.c](https://github.com/apple-oss-distributions/xnu/blob/main/bsd/kern/kern_descrip.c).
Unsupported kernels/filesystems fail closed. There is no classic POSIX-lock
fallback, so closing an unrelated descriptor cannot release admission.

Darwin's OFD byte locks and whole-file `flock` locks share a conflict domain
([kern_lockf.c](https://github.com/apple-oss-distributions/xnu/blob/main/bsd/kern/kern_lockf.c)).
.NET uses shared `flock` locks to emulate ordinary FileStream sharing
([runtime implementation](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/Microsoft/Win32/SafeHandles/SafeFileHandle.Unix.cs)).
An exclusive OFD lock even beyond EOF therefore denies the host's own streams;
Shared path-range probes also see unrelated runtime locks as conflicts. Linux's
independent lock domains do not have that behavior.

On macOS, every admission descriptor also opens
`.litedb-admission/<device>-<inode>.lock` below the data file's canonical parent
with native `open(O_CREAT | O_CLOEXEC | O_NOFOLLOW | O_NONBLOCK)`, without a managed
FileStream or `flock`. The data descriptor still
supplies physical identity, single-link and volume qualification. All admission,
path-binding and conflict probes use the sibling descriptor; the existing
distinct-descriptor enforcement probe remains mandatory. Symlink, hard-link and
non-regular lock files, and symlink lock directories, are refused. The same physical file in the same canonical parent
always selects the same lock inode, including through symlink aliases.

Lock files contain no database state and are never truncated, renamed or
automatically deleted, including after an unsuccessful open or last close.
Deleting an unlocked file would race an opener retaining its old inode and
allow two independent locks. Last close and process death release ownership by
closing the descriptor; the empty file may remain and is reused on reopen.
Only offline maintenance with no database participants may remove these files.
Writable admission requires access to this lock directory/file. Read-only Direct
opens the data descriptor and sibling lock descriptor read-only. If permissions
or a read-only filesystem prevent opening/creating the sibling lock, macOS can
fall back to a shared OFD lock on the read-only data inode **only when the data
file has no Unix write permission bits (0222)**. A writable data file still
refuses admission when its sibling lock cannot be obtained. The fallback keeps
process-local Direct/Shared exclusion and data-inode advisory locking; it does
not share the sibling inode's cross-process exclusion domain. Its writer fence
is the data file's OS permissions, which must remain non-writable to every
potential writer for the full open lifetime. Changing permissions, privileged
writers, and participants that can bypass that fence are outside this fallback's
guarantee. It creates no lock directory/file in a read-only directory.
Admission does not support external removal/replacement of its
lock directory or files while participants are live. Rebuild candidates live in the same
directory: retaining original and candidate locks across publication preserves
exclusion as the physical identity changes, without moving either lock file.

Admission uses reserved bytes starting at `long.MaxValue - 4096`, beyond all
database pages. Writable Direct takes an exclusive admission lock; Shared takes
a shared lock. Windows read-only Direct also takes an exclusive LockFileEx lock
using GENERIC_READ only. Unix read-only Direct first takes a shared read-only
Direct marker at admission + 2, then a shared admission lock, then probes for
other admissions through its own descriptor (OFD_GETLK ignores its own locks).
Both legacy and mapped Shared publish admission before probing the marker,
then claim their family byte (admission + 1 for legacy, + 3 for mapped) before
checking the opposite family. Neither family claim overlaps the read-only marker.
A racing incompatible
pair therefore cannot both succeed. Distinct read-only Direct processes also
refuse one another; compatible local facades reuse their host's admission.
Linux OFD read locks work with O_RDONLY and never require database write access.
The normal sibling protocol applies on Darwin except for the restricted
permission-fenced fallback above. Admission never locks page data. Hard links are refused because WAL/recovery
names are path-based. Symlinks canonicalize, and a file alias with its own WAL is
refused. File-only bind mounts and unqualified/network volumes are refused.

Shared takes a native admission reference at construction. Each of its opened
engines retains a local reference until it closes, so a supported leased Shared
reader can outlive facade disposal without admitting Direct. This retains no new
native transitions per operation. Deferred pin close releases the facade reference
after storage close. A failed native replacement invalidates local Shared admission;
public calls and engine creation reject that state. Native references finalize
after ordinary stream finalizers.

## Lifetime and platform limits

The data/WAL FileStream sharing modes use `FileShare.Read` for writers and
`FileShare.ReadWrite` for readers. The engine opens independent internal handles;
`FileShare.None` would deny its own readers/writers and admission handle. Native
admission excludes cooperating processes through the reserved byte, rather than
protecting page bytes from applications that do not follow this protocol.

Only the current facade count retains the host. A reader does not extend host
lifetime after all facades dispose. Engine-fatal failure stops every attached
facade; new attachment is refused until owners release and a fresh engine opens.
Read-only metadata getters can still read the live host after their facade is
disposed; transaction operations and maintenance reject that disposed owner.

The experimental coordinator shares this native admission layer, but retains its
own participant registry, mapping and result leases outside the Direct pool.
Its internal cores bypass Direct host attachment. Old LiteDB binaries do not participate in
this native protocol. Unix advisory locks do not exclude arbitrary applications
that ignore them. Windows/macOS runtime execution and device/power-cut testing
must be supplied separately from the Linux process tests.

## Safety evidence

`DirectProcessHost_Tests` covers refcounts, transaction ownership, incompatible
settings, maintenance, concurrent attachments and final-close admission.
`DirectHostSafety_Tests` covers failed opening, fatal storage failures, rollback,
symlink and hard-link policy. `DirectHostProcess_Tests` verifies native exclusion
after unrelated handle closes, Shared reader disposal, replacement, finalization
and process death, with repeated plain/encrypted recovery and indexed reads.
`DirectHostAdmission_Tests` covers first creation, plain/encrypted Direct/Shared
rollback and indexed read-only reopen, managed streams coexisting with native
admission, last-release lock availability, concurrent Shared child admission,
and persistent Unix lock-file identity and symlink refusal.
`DirectColdOpen_Tests` also forces creation through a parent symlink while the
canonical path's bootstrap is held. Bootstrap mutex names canonicalize at entry.
Mapped-test helpers use the engine's canonical path spelling on the same volume;
low-level authority headers and crash-temporary attribution therefore match
Darwin's `/var`/`/private/var` aliases. Windows live-byte assertions use explicit
read/write sharing so they observe the database without denying its existing writer.
The bounded child-process durable liveness cases exercise first open, index
creation, 1/4/8 writers, rollback, checkpoint, final close and two indexed reopens
on every modern-runtime OS, including Windows. Each operation checkpoint must
arrive within 20 seconds; a stalled child is killed during failure cleanup.
This is a correctness guard, not a performance threshold. The original Windows
benchmark's silent warmup timeout does not identify its blocked operation;
warmup progress diagnostics now precede opens and workload stages. Until hosted
Windows evidence distinguishes the failure, it is an unresolved liveness issue,
not evidence that admission's already nonblocking LockFileEx needs different flags.
`DirectHostReadOnlyAdmission_Tests` covers plain/encrypted mode-0444 files,
mode-0555 directories with no preexisting Darwin sibling, Windows ReadOnly
attributes, exact indexed payloads/rollback/sentinel data through repeated
byte-preserving opens, and both directions of cross-process mode exclusion.
`DirectProcessHostLifecycle_Tests` pauses final close while an unrelated database
opens and commits, retains same-file native exclusion, injects a close-checkpoint
failure, and checks both files through repeated recovery. Existing same-file
wait/reopen and finalizer tests remain unchanged.
`DirectProcessHostRekey_Tests` retains a maintenance lease across a completed
core rebuild (a deterministic regression that fails on `45b85dc2e`), attempts
attachment at every native rekey step, and forces second-thread attachment
after the rebuilding facade refreshes its services but before maintenance
releases. Plain/encrypted and canonical/alias cases repeat rebuild, test
completion-exception cleanup, and preserve both writers' acknowledged payloads,
secondary-index results and unrelated data through two cold reopens. Unix uses
a file symlink; Windows and Framework use an equivalent dotted path spelling.
`DirectHostAllocation_Tests` interleaves facade-owned allocations with a peer
header commit, checking commit/rollback, full payloads and indexes through two
reopens and further allocations.
`DirectColdReplacementProcess_Tests` verifies names and bytes stay unchanged
when admission is refused at three replacement boundaries.

Cold construction retains header/checksum/WAL validation, including rejection of
a converted WAL paired with a legacy header. `StartupHeader_Tests` and
`HeaderJournalFailure_Tests` cover partial reads, failed repair, immutable
read-only recovery and corrupt headers. Cache growth changes volatile allocation
only; `ColdCacheGrowth_Tests` and the existing default cache-limit assertion
preserve the budget floor. No persistent format or commit ordering changes.

These tests cover exceptions, corrupt input and process death. They do not
establish physical power-loss safety or directory-entry persistence; the latter
remains the existing #3011 gap. Cross-platform CI is required before integration.
