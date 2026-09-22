# Storage, transactions, and ownership

Use this for WAL/rebuild, cursors, locks, FileStorage, buffers, and disposal.
Apply [data safety](data-safety.md) first: preserve consistency through failures,
stop unsafe continuation, and make detected corruption visible.

## Transaction and cursor lifetime

- Reader cursors can outlive their opening thread. Retain the actual `Thread`
  identity for admission, lookup, cleanup, and completion guards. Managed thread
  IDs can be recycled; use them only for diagnostics.
- A foreign thread disposing a query must release that query's lease without
  committing, rolling back, or disposing the caller's independent transaction.
  Guard failures must leave the owner's transaction intact.
- Distinguish a transaction created by an operation from one it joined. A failed
  `BeginTrans`/join result or an occupied shared mutex alone does not establish
  explicit-transaction ownership. Include transparent public engine decorators.
- Keep publication, ownership handoff, and cleanup ordered. Cleanup after releasing
  a lock must not erase the next owner's state. Check abandoned-owner paths as
  well as ordinary completion.
- Cursors retain all snapshots they use, including referenced collections through
  INCLUDE. Upgrading a collection snapshot for writing must not dispose a snapshot
  still reachable by an active cursor or replay pipeline.

Read [explicit transactions](../explicit-transactions.md),
[reader ownership](../issue-2991-reader-ownership.md), and
[regression coverage](../transaction-regression-coverage.md).

## WAL, rebuild, and FileStorage

- Treat the data file and its WAL as one recoverable state. A restore that makes
  old data live while stranding its acknowledged WAL-only commits is incomplete.
  Exercise failures in both restore orders and repeated recovery attempts.
- Do not delete backups or replacement candidates until a complete usable state
  has been established. Distinguish corruption from transient I/O failure before
  repairing or discarding pages. Preserve the original failure and recovery data.
- File-backed opens check the rebuild recovery marker before upgrade, automatic
  rebuild, or database creation. Create and flush it before installation renames;
  clear it only when a complete original data/WAL pair or completed replacement
  is confirmed live. Repeated recovery failures must block direct and shared opens,
  including when the live file is missing. Preserve the original exception and
  rollback errors; synchronize retained engine settings if the replacement remains
  live. See [rebuild recovery](../rebuild-recovery.md).
- Changes to transaction-ID allocation, checkpoint, or WAL reuse need tests for
  abandoned/unconfirmed pages, live snapshots, and crashes between publication
  steps. Reusing bytes must not resurrect an older transaction's pages.
- FileStorage metadata and chunks must remain consistent through upload, delete,
  append, metadata updates, throwing streams, rollback, and explicit transactions.
  Include incremental `OpenWrite` and active cursors. Lock-order analysis must
  include locks already held by the caller, not only the helper's local order.
- Do not hide transactional side effects inside an enumerable that assumes a
  decorator will consume it once, lazily, and only after acquiring a lock.
- For file-ownership changes, test aliases and sidecar identity, read-only sharing,
  rebuild/reopen, caller streams, platform fallbacks, and crash cleanup of scratch
  files. Lexical path normalization alone does not prove physical-file identity.

## Buffers and cleanup

Every pin, pooled buffer, cursor, and underlying stream needs a clear owner and
release path for completion, early termination, exceptions, and repeated disposal.
Disposal admission must be atomic; lazy resource publication must coordinate with
concurrent disposal. Logging/callback failures must not skip cleanup or replace the
original error. Respect caller-stream ownership.

Borrowed views cannot outlive the page/buffer that backs them. `byte[]` storage
marshalled into structs may contain only unmanaged data; keep managed references
in GC-visible storage. Do not remove cache lifetime synchronization as a mechanical
performance tweak. See [memory lifecycle audit](../memory-lifecycle-audit.md).
