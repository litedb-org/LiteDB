# Direct process host

File-backed Direct connections share a process-owned storage engine.
Transactions remain synchronous and thread-bound; this does not introduce
thread-independent transaction handles.

One physical file has one live storage engine per loaded LiteDB assembly.
Opening a compatible file-backed `LiteDatabase` or `LiteEngine` attaches an
engine facade: it references the same storage services and has a fixed owner
view of the transaction monitor. The registry uses physical identity, weak
references, canonical data/WAL paths and a facade reference count. Open,
last-close and maintenance serialize at a cold-path registry gate. Last close
uses the existing engine disposal path before releasing native admission.
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

The native lock implementation uses Windows LockFileEx;
Linux F_OFD_SETLK/F_OFD_GETLK (37/36); Darwin OFD commands 90/92, including the
arm64 variadic ABI. Darwin does implement OFD commands; see Apple's
[fcntl.h](https://github.com/apple-oss-distributions/xnu/blob/main/bsd/sys/fcntl.h).
Unsupported kernels/filesystems fail closed. There is no classic POSIX-lock
fallback, so closing an unrelated descriptor cannot release admission.

Admission locks one byte at `long.MaxValue - 4096`, beyond all database pages,
exclusive for Direct (including read-only Direct), shared for Shared. It never
locks page data or the whole file. Unix Direct needs a writable native descriptor
even when its engine is read-only. Hard links are refused because WAL/recovery
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

The experimental coordinator continues its existing admission protocol; it is
not converted into the Direct pool. Old LiteDB binaries do not participate in
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
