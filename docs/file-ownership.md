# File ownership

Filename-backed engines hold an OS lock on the actual data file. One writer owns
the file exclusively; independent read-only engines can share a stable file.
Mixing a live Direct writer with another Direct or Shared engine fails before
the contender accesses the WAL. Use Shared connections when multiple clients
need coordinated writes, and dispose engines when their work is complete.

Use one stable filename for the lifetime of a persisted database, including
later opens. Companion WAL and backup names still derive from that filename.
The physical-file lock rejects simultaneous opens through aliases; it does not
make reopening through a different symlink/hard-link name safe when the original
name has an outstanding WAL. Move/copy the complete database and WAL together.

The engine snapshots storage/access settings and resolves relative filenames when it is
constructed. Later changes to the caller's settings or working directory do not
retarget the database or change its access mode. ReadTransform remains a live
callback setting. Shared engines keep their own
settings snapshot and retain rebuilt password/collation settings for later opens.
Supplying both DataStream and Filename is rejected before opening any files;
choose one data source so its WAL and rebuild paths cannot target another database.

Rebuild rejects active transactions before changing the database. It retains
ownership during replacement, checkpoints the original generation before normal
rebuild, and installs the replacement atomically. The resulting data backup is
self-contained, including commits that were only in the WAL before rebuild;
normal rebuild no longer needs a separate WAL backup, even with CHECKPOINT=0.
An I/O failure can close the
engine; dispose it, resolve the storage error, and reopen to recover. Automatic
reopening after an uncertain storage failure is not guaranteed.

Windows uses byte-range locks; Linux and Darwin use open-file-description locks.
Older Linux kernels and FreeBSD use handle-scoped flock
locks with compatible stream opening. A filesystem or sandbox that cannot
provide the selected locking primitive refuses to open: an unsupported
lock must never silently admit independent cached writers.

Filename mode requires native file identity and ownership-lock APIs. Browser and
WASI hosts should use a caller-owned DataStream, or an in-memory/temporary database.
The host must coordinate persisted streams shared by separate runtime instances.
A pathname-only lock cannot safely distinguish aliases on virtual filesystems.

Disk-backed sorts use private scratch streams. Windows deletes their files when
the handle closes; Unix immediately unlinks them while keeping the descriptor
open. The OS reclaims the storage even when the process terminates abnormally.
