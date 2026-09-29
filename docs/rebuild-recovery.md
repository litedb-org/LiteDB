# Rebuild installation and incomplete recovery

Rebuild prepares and checkpoints a complete replacement before installing it.
The original data and WAL are moved to backup paths. If installation fails,
LiteDB tries to restore the original pair or publish the completed replacement.
It never restores the original WAL beside replacement data. When a replacement
remains live, the shared connection retains its requested password and collation.

File-backed engine admission and rebuild share a process-wide named mutex for the
normalized database path. Opening waits at most 60 seconds for an active owner;
explicit rebuild retains ownership from engine close through replacement and
reopen. Opening recovery and v4 upgrade retain the same ownership through their
entire open. The OS releases this transient claim on process death. It does not
replace the durable recovery marker described below.

On runtimes without named-mutex support, ordinary Direct opens (including
read-only opens) retain their previous behavior without this admission mutex.
Shared/Coordinated mode, explicit rebuild, and opens requesting `AutoRebuild` or
`Upgrade` still require it and fail if unavailable. Permission errors and mutex
timeouts are not treated as unsupported platforms. The durable recovery marker
is checked even when an ordinary Direct open cannot use a mutex.

Direct rebuild additionally holds exclusive handles on the original data and WAL
through construction and installation. Its file reader borrows these handles,
so finishing the scan cannot admit another writer before the marker is created.
The completed candidate is also held exclusively through installation and
rollback. Windows handles allow rename/delete, but not competing reads or writes;
Unix retains exclusive locks on the open inodes through rename. Rebuild checks
that those locks actually exclude a second handle and refuses unsupported or
disabled locking before reading records. An existing Direct reader or writer
must close before rebuild can acquire these handles. Shared/Coordinated rebuilds
retain their existing mutex, reader-admission and cached-handle protocol.

Use one consistent data/WAL path for all connections. Absolute and relative paths
that normalize to that same path share admission. Filesystem symlink/hard-link
aliases and older engines do not share the path-based protocol and must not run
concurrently with replacement; aliases can also name different WAL/marker files.
The physical claims exclude handles to the claimed inodes, but do not establish
equivalence of those sidecar paths. Rebuilding a filename combined with
caller-provided data/WAL streams is rejected without closing those streams.

Before the first rename, LiteDB creates and flushes a recovery marker next to the
database (`data-rebuild.db` for `data.db`). Its text records the live, backup and
replacement paths, including any numbered suffixes. It contains no passwords.
Only a confirmed complete original pair or replacement allows marker removal.

If recovery itself fails, the marker remains. Direct and shared file-backed opens
throw `LiteException.REBUILD_INCOMPLETE` (139), including read-only opens and opens
requesting upgrade or automatic rebuild. Checking happens before opening or
creating database files: a missing canonical file cannot become an empty database,
and original data missing its required WAL cannot silently serve stale rows.
An empty or partially written marker also blocks access. Marker access errors
propagate rather than being treated as evidence that recovery completed.

Rollback is deliberately bounded. It settles on the original pair, else on the
completed replacement, else it stops and keeps the marker: every further
compensation step would itself be fallible. One failed rollback move still
leaves a complete, accessible database; only two or more can end guarded.
A replacement that will not be published is deleted, together with its WAL: when
building it fails, when the marker cannot be created, and when the original pair
is restored. It is a full copy of the database that may lack the original's
encryption. If it cannot be deleted, that is reported with the other recovery
errors, but it does not guard the database: blocking an intact original would
not remove the file.

The original rebuild exception is preserved. Its `Data["LiteDB.Rebuild.LiveState"]`
reports `original-restored`, `replacement-published` or `incomplete`. Additional
recovery errors are in its `Data["LiteDB.Rebuild.RollbackErrors"]` aggregate. A failure to remove the marker
also leaves access blocked, even if a complete database is already live. Removal
waits up to five seconds for a sharing violation to clear, because virus scanners
and sync clients briefly open a newly written file on Windows.

## Recovering a guarded database

1. Stop users of the database. Preserve copies of the marker and every live,
   backup, WAL and replacement file before changing anything. Use the exact paths
   recorded in the marker; suffixes can differ after multiple rebuilds.
2. Recover to a separate filename. Either copy the original data **and its matching
   original WAL**, or use the completed replacement with the password and collation
   requested by rebuild. The original data can still be at the live path if its
   retraction failed. Never combine replacement data with the original WAL.
3. Open and verify the recovered copy, including acknowledged WAL-only commits.
   Checkpoint and close it before publishing it at the canonical path. Preserve the
   original evidence until recovery has been verified.
4. Restore the verified complete database, ensuring no incompatible WAL remains
   at the live WAL path. Remove the recovery marker **last**, then reconnect with
   the settings matching the recovered database.

The marker is a conservative access guard, not an automatic recovery journal.
Interrupted installations require verification; removing the marker alone does
not repair a missing data/WAL pair. Caller-provided streams and older LiteDB
versions do not consult this filename-based guard, so do not use them to bypass it.

Regression coverage includes the repeated-failure cases from #2979, an exhaustive
installation/rollback matrix, marker creation and cleanup failures, reads and
writes through reused shared connections, fresh opens, encryption and collation
changes, and original-pair/replacement recoverability.

`RebuildOwnership_Tests` adds competing data/WAL handles, exact backup preservation,
claim-cleanup failures, retained replacement credentials, read-only sharing and
caller-stream ownership. `RebuildOwnershipProcess_Tests` forces a second process
to contend before the marker, during installation, and during the handoff to
reopen, for both plaintext and encrypted files. It also kills the owner twice
before marker publication, verifies unchanged original data/WAL and successful
retry, and kills it after publication to verify that abandoning the mutex does
not bypass the persistent marker. Further process cases park the recovery rebuild
service after claiming the source, during installation and after closing its
claims while Direct, Shared and Coordinated processes wait; their writes are then
acknowledged and survive a cold reopen. `RebuildOwnershipModes_Tests` races real
Direct, Shared and Coordinated engines in one process and observes each opener
blocked in the admission wait, including against a Shared rebuild through its
reopen. `RebuildAdmissionPlatform_Tests` covers runtimes without named mutexes.
CI runs these classes on Linux, Windows and macOS for every PR. These tests model
process death and injected cleanup failures on supported local filesystems, not
storage devices ignoring flushes or uncoordinated access through different
sidecar identities.

## Opening recovery and partial-document salvage

When a normal open fails with corruption found by opening index validation (see
the release notes for #3022), use a writable file connection with `AutoRebuild=true`:

```csharp
using var db = new LiteDatabase(new ConnectionString
{
    Filename = "damaged.db",
    Connection = ConnectionType.Direct,
    AutoRebuild = true
});
var errors = db.GetCollection("_rebuild_errors").FindAll().ToArray();
```

This is the recovery entry point when construction fails before an instance
`Rebuild()` can be called. It uses the replacement/recovery-marker protocol above,
retains the original backup, and records the opening diagnostic along with salvage
errors in `_rebuild_errors`. Preserve the backup and review that report: a successful
salvage does not mean every original record was readable. Each report entry has a
`stage`: `opening` describes the reason recovery started, while `salvage` describes a
problem encountered while rebuilding. The opening entry can exist even when every
record was recovered. Known document failures identify `pageType=Data` and the data
page ID, index-order failures identify `Index` and the index page ID, and unclassified
structural failures use `Empty` with a null page ID.

Opening validation is inspection: a rejected source is closed without a checkpoint
and without the invalid-state mark, so an open that may not replace it leaves data
and WAL byte-for-byte unchanged. Only a writable, file-backed `AutoRebuild` open
recovers, and only once: it consults Shared-mode replacement admission, runs the
rebuild with the opening error, then opens the installed candidate without allowing
another opening rebuild. Cleanup errors block replacement and retain the original
opening exception in an aggregate. Replacement releases its structural scope before
reopening; a failed candidate reopen closes without stamping the rebuilt file
invalid and is reported as it is. A failure of the recovery itself keeps the damage
that required it: a `LiteException` is wrapped ("The automatic rebuild failed: ...")
with its `Data` copied, and any other exception keeps its type with the damage in
`Data["LiteDB.RebuildCause"]`. `ReadOnly=true` and caller-provided streams never
trigger a replacement, also when the invalid-state flag is already set, and a
read-only engine never writes that flag. Runtime corruption found after a
successful open still marks a writable file for rebuild on its next open.

The rebuild reader keeps the readable fields of a damaged document when they still
identify it (an `_id` that is not null, MinValue or MaxValue) and offers them after
every complete document of the collection. A part is not inserted when its `_id`
belongs to a complete document or to a part already kept, when it would repeat a key
of a unique index, or when its index keys are invalid or cannot be computed; each
case is reported in `_rebuild_errors`, and a rejected part leaves its `_id` to a
later one. A kept part is reported too, because it reads like a complete document.

`Issue3022LegacyDamage_Tests`, `Issue3022WalRecovery_Tests` and
`Issue3022OpeningSafety_Tests` cover source preservation after every refusal,
first-open salvage with byte-identical data/WAL backups, cleanup failures, the
bounded candidate reopen and read-only refusal. `LegacyDamagedDocument_Tests` uses
files written by the 5.0.21 package (LiteDB-Artifacts fixtures) for the default
refusal, first-open salvage, cause preservation, caller streams and the
partial-document, unique and duplicate-`_id` cases; `Issue2417_Tests` and
`Issue2812Recovery_Tests` keep the plain/encrypted loop and collation recovery.
