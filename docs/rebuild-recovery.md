# Rebuild installation and incomplete recovery

Rebuild prepares and checkpoints a complete replacement before installing it.
The original data and WAL are moved to backup paths. If installation fails,
LiteDB tries to restore the original pair or publish the completed replacement.
It never restores the original WAL beside replacement data. When a replacement
remains live, the shared connection retains its requested password and collation.

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

### Mapped Shared maintenance

Opt-in `SharedOneFile` maintenance uses the same backup/candidate installation
and bounded rollback, with a checksummed binary recovery guard containing exact
file identities. It first flushes a pending record and durably publishes the
guard, then syncs installation renames. Before marker removal, an offline mapped
opener holding bootstrap, exclusive DMS, main and WRITE can repeatedly restore
the identified original data/WAL pair. It removes the guard only after that pair
is established. A pending record alone means no installation rename was admitted.
Unknown/damaged/partial records still refuse; Direct and legacy Shared never
automatically interpret this extension. Preserve guarded files for inspection.
See [mapped ownership](shared-one-file-ownership.md#phase-3-c-maintenance-and-final-close)
for rekey and final-close ordering and the tested fault-model limits.

Mapped guard recovery requires a writable open. `ReadOnly` or internal
`ReadOnlyStorage` refuses with error 139 before recovery creates coordination
files or changes any bundle bytes, even with `Upgrade=true`. Relative paths,
`./` spellings and parent-directory symlinks resolve to the canonical guard
namespace before lookup and bootstrap. Moving a guarded bundle elsewhere does
not authorize rewriting its recorded absolute paths: move it back or preserve it
for offline inspection. Foreign identities, hardlinks and malformed/checksum-invalid
records are refused before the first recovery mutation; record reads are bounded
to 64 KiB before allocating their buffer.

Explicit `Upgrade=true` retains the Direct v7 contract: after protected header
inspection, mapped mode opens a writable maintenance core under main/WRITE and
sole-participant DMS, completes the rebuild, and then applies the requested
read-only facade. A modern file with a no-op upgrade retains read-only storage
semantics. Upgrade does not bypass an existing incomplete-rebuild guard.

`SharedOneFileRebuildPowerLoss_Tests` extends the existing
`PromotionPowerLossStream` byte model with `RebuildDirectoryCrashDevice`.
It enumerates all pre/post-barrier namespace images from the real forward,
bounded-rollback and recovery code, then explores interrupted recovery to a
fixed point. Every reachable image must recover without refusal and preserve
complete committed documents, indexed results and an unrelated indexed
collection through two cold opens. It includes absent original WALs, plaintext
and encrypted files, every lost/torn pending-write prefix, every damaged guard
prefix, and independently reordered candidate/guard cleanup deletions. The
committed-state oracle is checked against a deliberately stranded WAL.

This is a finite modeled-power-loss test of the guard protocol, assuming atomic
same-directory rename and successful file/directory sync. Published guards cannot
tear under those assumptions; torn published guards are separately tested as
corrupt input and must preserve the bundle. Inode identities are opaque tokens
remapped consistently when materializing copies. It is not a device-reset test,
a model of the entire storage stack, or filesystem/ARM qualification.
