# File and persisted-data compatibility

Use this for formats, index encodings, comparers, serialization changes, and upgrades.
The [data-safety requirements](data-safety.md) govern every migration: avoid forced
legacy rebuilds, make transitions atomic and repeatable, and support crash/power-loss
recovery explicitly.

## Establish the boundary

Compatibility includes stored BSON shapes, field/key spellings, comparer ordering,
index keys, WAL records, and query semantics. A file that opens successfully can
still return wrong indexed results. Test old-writer/new-reader and, where supported,
new-writer/old-reader behavior using actual released binaries and persisted files.

Changing numeric ordering, date decoding, dictionary-key formatting, enum-array
representation, or collation can invalidate existing indexes. Compare indexed and
unindexed reads, uniqueness, updates/deletes, and reopen. Either preserve the old
contract or design an explicit version/migration boundary; do not silently stamp
old indexes as compatible.

Use the current branch's format constants and merged design as authoritative.
Version numbers proposed by concurrent PRs can collide. Do not copy an unmerged
feature's format number or migration policy into general repository guidance.

## Current vector boundary

Legacy-mode ordinary databases remain format v8 and open without migration. The first vector
write requires at least v9 before vector pages enter the WAL.
Rollback, replay, and checkpoint must never downgrade that version. `Upgrade=true`
still rebuilds v7 files before applying read-only access. Durable flushes must reach
the actual file through encryption and caller-stream wrappers.

Run `python3 scripts/test-vector-compatibility.py` for ordinary v8 round trips and
LiteDB 5.0.21 rejection of vector files, including encryption. Use
`python3 scripts/test-v8-differential.py` for current/released-engine mutation and
reopen comparisons. Details: [vector compatibility](../vector-query-compatibility.md).

## Compact document storage

`CompactStorageMode.Auto` writes compact documents when beneficial and lazily
promotes existing v8/v9 files to v10; `Legacy` is the explicit compatibility policy.
New Auto databases start at v10. Route DataBlock reads through
`DocumentStorageCodec`, never public BSON alone. Schema pages belong to the
collection transaction; clear read-version catalog caches when WAL versions reset.
Run `python3 scripts/test-compact-compatibility.py` alongside the vector script.
See [compact storage](../compact-document-storage.md) for layout, bounds,
downgrade options, and benchmark results.

Format promotion journals the persisted header in checksummed WAL padding before
overwriting it. Keep the journal durable (including the Unix directory entry),
recover before header validation, and sync WAL retirement before slot reuse.
Encrypted recovery pages must retain their raw blank-page prefix so torn records
cannot be replayed as transactions. Run `compact-power-loss` when changing
promotion, recovery, encryption, or WAL retirement.

## Migration and failure questions

Answer these in a format-changing PR. Atomicity, retry, and crash/power-loss behavior
require tests; document any remaining validation gaps explicitly:

- Is conversion automatic or opt-in, and what happens on read-only open?
- Is it a header change, lazy conversion, index rebuild, or full rewrite? Can
  old/new representations coexist, including mixed shapes in one collection?
- What disk space, I/O, memory, and startup time are required? Test `LIMIT_SIZE`,
  preallocated files, encryption, custom collation, and insufficient capacity.
- What happens at each partial write, failed durable flush, rename, replacement,
  and repeated crash during recovery? Can reopening resume or recover a complete
  state? What artifacts must remain available for recovery?
- Which old readers reject the new state? Does any partial marker or corrupt
  modern header incorrectly fall back to legacy decoding?

Do not substitute ordinary `Flush()` for durable flush after an error and retain
the same durability claim. A successful reopen is not a power-loss test. When
testing large logical files, use sparse/fault-model streams or bounded fixtures
instead of writing hundreds of gigabytes to the developer's disk.

See [storage ownership](storage-ownership.md) for data/WAL replacement and
[validation](validation.md) for independent crash and compatibility oracles.
