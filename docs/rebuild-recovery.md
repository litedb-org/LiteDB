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

The original rebuild exception is preserved. Additional recovery errors are in
its `Data["LiteDB.Rebuild.RollbackErrors"]` aggregate. A failure to remove the marker
also leaves access blocked, even if a complete database is already live.

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

Regression coverage includes the repeated-failure cases from #2979, a 192-case
installation/rollback matrix, marker creation and cleanup failures, reads and
writes through reused shared connections, fresh opens, encryption and collation
changes, and original-pair/replacement recoverability.
