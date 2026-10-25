# Operating Shared mapped reads

On .NET 8+, repeated Shared queries can retain a read-only engine and use a small
memory-mapped status page to avoid reopening under the database mutex. Writers
publish changes to that page; cached readers briefly yield while a writer is
active. This changes neither the database/WAL format nor commit durability.

## Opt-out and diagnostics

The optimization is enabled automatically on qualified configurations. To disable
it for new connections, set this switch before creating any connection:

```csharp
AppContext.SetSwitch("LiteDB.DisableSharedMappedReads", true);
```

Alternatively, set `LITEDB_DISABLE_SHARED_MAPPED_READS=1` (or `true`) before process
startup. An explicit AppContext value takes precedence. Disable it in every
participating process for a predictable protected-read deployment. An opted-out
reader can read without revoking other participants; its first writable open must
revoke their mapped admission. Already accepted streams retain their leases and
snapshots. A failed revocation prevents the write.

`SharedEngine.CoordinationFallbackReason` is available in production. Keep the
`SharedEngine` passed to `LiteDatabase` to inspect it. A non-null value explains
why that connection could not attach (configuration, architecture, volume, names,
or an attachment exception). Null means no fallback has been recorded; it does
not prove the cache is active. On older target frameworks the property is null
and all reads use the protected path. It is not a global status or a counter of
peer revocations; broader diagnostics are tracked in #3019.

On Windows, control publication and attachment retry native sharing/lock violations
up to four times with 10 ms delays. Unknown formats, access denial and other I/O
errors are not retried. If attachment still fails, that connection falls back for
its lifetime. A fallback writable open publishes `-shared-disabled` before mutation,
which disables admission for every peer until the old authority can be retired.
Close all participants and reopen after correcting the environment. Never delete
sidecars to bypass a live fallback.

## Files, backup and deployment

The database directory may contain `<filename>-shared-live` (128 bytes),
`<filename>-shared-state` (4096 bytes), and `<filename>-shared-disabled` (8 bytes),
as well as the existing `<filename>-readers/` lease directory. These are ephemeral
coordination files, not durable database state. The directory must permit creation,
sharing, mapping and rename for the optimization to work. Antivirus or indexing
software holding exclusive handles can cause fallback; exclude the database's
working directory from such interference where deployment policy permits.

Control publication uses attributed `.ldb-<path-tag>-<random>` temporary files in
the same directory. Under the database mutex, subsequent publication/retirement
cleans empty or recognized abandoned temporaries only after an exclusive handle
proves no publisher is using them. Complete headers must also match the database.
Unknown, malformed and old untagged `.ldb-*` files are preserved. Such leftovers
are at most 4 KiB per interrupted control publication; remove them manually only
with all participants closed and their origin established.

Do not delete, replace, restore or synchronize live coordination files. Do not run
a live database in a cloud-sync folder or share it between machines: this protocol
requires one host's mutex and sharing locks. Ephemeral sidecars need not be copied
in a backup made after every connection has closed. Database and WAL files remain
authoritative: follow the [backup rules](release-notes.md#snapshot-aware-checkpoints-format-v13),
including capturing a consistent data/WAL pair when the WAL is nonempty. Copying
live files independently is not a backup protocol.

## Qualified environments and identity

Mapped admission currently requires .NET 8+ on x86, x64 or ARM64 and a fixed local
ext2/3/4, XFS, Btrfs, NTFS, ReFS or APFS filesystem. Windows also requires the shared
handle configuration described in the protocol. Overlayfs, tmpfs, ZFS, WSL drvfs,
network filesystems and unrecognized volumes use protected reads. In Docker, the
root overlay is excluded; use a bind-mounted directory whose reported underlying
filesystem qualifies. A bind mount alone does not guarantee qualification.

On Unix, **all participating processes must have working OS file-sharing locks**.
`DOTNET_SYSTEM_IO_DISABLEFILELOCKING=1` / `System.IO.DisableFileLocking=true` is
unsupported for Shared and Coordinated connections; this build rejects the
configuration before opening the reader registry or retiring an authority. The
mapped-read opt-out does not remove the existing lease protocol's locking
requirement. Either enabled disabling knob is rejected, even with a conflicting
false value in the other: runtime precedence differs between .NET versions. Set runtime configuration before process startup; changing it after
the runtime has initialized I/O is unsupported. This check cannot protect against
an older or external participant that bypasses locks.

Use the same absolute filename and mutex naming strategy in every participant.
The binding folds case on Windows and macOS, matching the existing mutex identity
on their normally case-insensitive filesystems. Linux bindings retain case to
separate distinct names on case-sensitive volumes, even though those names share
the case-folded mutex. Case folding does not resolve symlink or hard-link aliases;
those remain unsupported. Close all participants before renaming or replacing a database.

## Scheduling and retained memory

Pacing is per connection. Under writer pressure, cached query work accrues a delay
budget, with at most 10 ms requested per query and 50 ms bounded debt/credit.
`Thread.Sleep` blocks the synchronous caller; applications running many queries
on ThreadPool workers should account for that occupancy. The OS may delay a
thread longer than requested. This is not a writer-latency guarantee.

A connection already attached can announce before waiting for the mutex. A fresh
writer can safely attach only after its first mutex acquisition; a directly writable
call announces immediately afterwards, before engine open/recovery. `BeginTrans`
cannot predict later writes, so its first actual write starts pressure. The hint remains active
while that writer owns an open engine, including a long transaction, because
cached reads can consume CPU during its work. It expires after 100 ms and is
cleared at completion if it still belongs to that writer. Pragma getters,
transaction starts with no writes, and pinned read calls do not request pressure
or retire the cache; an actual write does.

Retained state belongs to each connection. The 4 MiB page-cache and opening-WAL
thresholds are component limits, not a process memory cap. Idle snapshots expire
after 100 ms; active streams retain their normal leases. Many connections multiply
memory and native handles, and streaming snapshots can outlive cache replacement.
Process-wide sharing/pooling is tracked separately in #3017.

## Running mapped-path tests

Mapped tests choose a writable qualified directory from the temporary directory,
test output directory and current directory. Set `LITEDB_MAPPED_TEST_DIRECTORY`
to choose one explicitly, for example a qualified bind mount in Docker. If no
candidate qualifies, mapped tests report a skip with the qualification reason;
ordinary fallback and low-level format tests still run. This does not relocate
unrelated I/O/latency reproductions or override runtime filesystem qualification.
