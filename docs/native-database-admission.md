# Native database admission

Admission protects the compatibility of file-backed LiteDB users before storage
opening, recovery or upgrade can write. It carries no persistent database state.
Old `-shared-mode` artifacts are ignored, never read, truncated or deleted.

## Ownership and compatibility

A registry keyed by the opened file's physical identity owns the native handle.
Windows uses the volume serial and 128-bit file ID; Unix uses device and inode.
Each successful acquisition returns a separately disposable, finalizable reference.
Only the final reference removes the entry and closes its handle. The registry
never roots reference objects. Its monitor orders retain/release and replacement.
Acquisition and disposal need not run on the same managed thread. Abandoned leases
use critical finalization: ordinary FileStream finalizers can flush buffered writes
and must finish before the admission handle closes.

| Participants | Compatible? |
| --- | --- |
| Direct writer / another process | No |
| Direct writer / Shared, including read-only Shared | No |
| Shared / Shared with the same effective mutex strategy | Yes |
| Shared / a different mutex strategy | No |
| Standalone Direct reader / standalone Direct reader | Yes |
| Standalone Direct reader / Direct writer or Shared | No |

Compatible local owners reuse the process's admission handle. This does not merge
independent engines' transaction monitors, caches or WAL indexes: a second local
writable Direct engine remains incompatible. Share the engine for concurrent
operations, or use Shared connections. Independent Direct read-only engines are
compatible. Shared operation engines and escaping snapshots retain connection
admission through the existing lifetime wrapper.

`ReadOnly=true` opens an existing database with read access and never creates a
mode artifact. Read-only Shared can use protected reads when mapped coordination
files are unavailable. `Upgrade` and `AutoRebuild` require writable admission even
when the eventual connection is read-only. Coordinator snapshots retain their
host-issued reader lease instead of acquiring conflicting standalone admission.
Memory databases and caller-owned streams keep their existing ownership contract.

The registry belongs to one loaded LiteDB assembly. Separately loaded copies or
AppDomains do not share managed engine state; the OS still excludes conflicting
handles, so an exclusive opener in another copy fails closed.

## Native protocol

A one-byte admission range starts at `Int64.MaxValue - 4096`, beyond LiteDB's
supported database address space. Direct writers lock it exclusively; other users
lock it shared. Three following bytes identify shared families: the default/URI
mutex strategy, the explicit SHA1 strategy, and standalone Direct readers. Under
a short physical-identity mutex, an entrant checks for locks in incompatible
families and acquires its own shared family lock. Equivalent default and URI
mutex names remain compatible. These bytes are lock addresses; no file bytes are
written or file length changed.

Windows uses nonblocking `LockFileEx` and `UnlockFileEx` with a read handle sharing
read/write/delete. Unix uses nonblocking open-file-description `fcntl` locks:
Linux `F_OFD_SETLK/F_OFD_GETLK` and the macOS equivalents. POSIX process locks are
unsuitable because closing an unrelated descriptor can release them. `flock`
would conflict with .NET's independent file-sharing locks. Unix descriptors use
`O_CLOEXEC`; handles are not inherited by child executables.

A second descriptor probes the first lock before any storage engine is admitted.
A primitive that reports success without enforcing locks fails closed. Native
errors also fail closed; there is no process-local fallback. Short named mutexes
serialize acquisition and replacement only; they are released on their acquiring
thread and are never the connection's lifetime admission lease.

Supported filesystem policy is deliberately conservative: local NTFS/ReFS on
Windows; ext2/3/4, XFS, Btrfs, tmpfs and local overlayfs on Linux; APFS/HFS on macOS.
Network shares and unrecognized filesystems are refused. Unix currently requires
x64 or arm64 and a kernel implementing OFD locks. Windows uses its native ABI on
both x86 and x64. Overlayfs requires local backing storage with functioning OFD
locks; a successful local probe cannot certify remote-server or device behavior.
The independent Shared coordination protocol still requires .NET file-sharing
locks to be enabled.

Darwin x64 uses the INODE64 stat entry points; arm64 uses the native stat ABI.
The arm64 open/fcntl bindings place their variadic argument on the stack, as
required by Apple's ABI.

Sources: [LockFileEx](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-lockfileex),
[Linux OFD locking](https://man7.org/linux/man-pages/man2/F_OFD_SETLK.2const.html),
[Darwin fcntl definitions](https://github.com/apple-oss-distributions/xnu/blob/main/bsd/sys/fcntl.h).

## Paths and replacement

Resolve symlinks and directory aliases before binding data, WAL, recovery marker,
Shared mutex and reader paths. Dangling file symlinks are rejected before creating
a target, so they cannot bypass its rebuild marker. Native identity also verifies that the admitted
handle still names the file at the canonical path. An alias with a separate pre-existing WAL is rejected; its own recovery marker
also remains authoritative. Recover such legacy alias data/WAL pairs together
before switching to canonical paths. Hard-linked databases fail
closed: their alternate path names could choose unrelated WALs for one inode.
External unlink/rename, changing links during use, and concurrent use by executables
with older admission protocols remain unsupported. Stop all users when upgrading
the library. The existing pre-guard `-shared-live` exclusion remains as a limited
compatibility check, not a guarantee for older executables.

Replacement takes a scoped canonical-path mutex and upgrades the old native lock
to exclusive under its physical-identity mutex. If another process has Shared
admission, even while idle, replacement fails before creating the recovery marker
or renaming storage. An idle remote handle cannot be transferred to a new inode.

The completed candidate is locked before publication. Both native locks remain
held through the existing recovery marker, source/WAL backups, candidate rename
and rollback. The path mutex orders initial creation with this interval: a waiter
must recheck the recovery marker before creating or opening storage. It cannot
manufacture an empty live file between the renames.

Once installation/rollback settles, the process registry follows the resulting
live inode and releases the unused candidate/backup handle. Shared admission is
restored under the identity mutex while the path mutex remains held. Local idle
Shared connections retain references to this same entry and reopen the live file
on their next operation. A missing/incomplete live pair keeps the existing
recovery marker. Process death releases locks; the marker still blocks incomplete
installations. Native cleanup failure preserves the primary installation error and marks local
admission faulted. A published replacement still reports its live state so callers
retain the correct password and collation. No new data/WAL durability or recovery
format is introduced.

## Validation

The fault model covers native process termination, failed acquisition, injected
unsupported filesystems, failed lock conversion, installation/rollback I/O failures,
and competing opens.
It assumes OS lock enforcement on supported local filesystems and the existing
rebuild marker/device-flush durability contract. Process death is not a simulation
of power loss or a dishonest filesystem server.

| Invariant | Automated evidence |
| --- | --- |
| Incompatible processes fail before mutation; immediate opens after owner death succeed. | `NativeAdmissionProcess_Tests`: both opening orders, Direct/Direct, read-only modes, repeated kills, preserved indexed records and unrelated collection. |
| Shared processes coexist; the last owner releases admission. | Five independent Shared processes, serial owner deaths, and local references surviving engine disposal with foreign-thread release. |
| Local refcount races and failed/abandoned opens cannot lose or strand the lock. | `NativeAdmission_Tests`, `NativeAdmissionFinalizer_Tests`, `SharedAdmissionLifetime_Tests`, `SharedModeGuardFailure_Tests`; concurrent retains, repeated/foreign disposal, finalization ordering, acquisition faults and wrong-password opens. |
| Read-only storage is unchanged and requires no writable mode artifact. | `NativeAdmissionAliases_Tests`, Windows ACL-denial tests, `DirectModeAdmission_Tests`, Shared admission review and lifetime tests. |
| Aliases do not select a second WAL or admission identity. | Lexical aliases, Unix file/directory symlinks, Windows/Unix hard-link rejection, cross-process conflicts and rebuild through an alias. |
| Replacement cannot transiently admit a conflicting user or strand recovery data. | Cross-process pauses and kills at installation boundaries, a prechecked opener in the rename gap, failed downgrade exclusion, remote idle Shared rejection, local retained admission; existing rebuild install fault matrix, crash suite and #2979 repeated rollback recovery tests. |
| Unsupported locking never silently weakens writable safety. | Injected unsupported volume, actual conflict probes on every initial lease, existing runtime-disabled-locking tests. |

The process harness is packaged by the existing Windows/Linux/macOS CI matrix and
runs with the test host's selected runtime and architecture. Local Linux results
do not establish execution on Windows/macOS or Framework CLR. See the task's
validation report for the exact executed configurations and any outstanding gaps.
