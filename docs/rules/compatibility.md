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

## Vector File Compatibility
New files use format v10 with data-page and WAL checksums. Writable v8/v9 opens
recover/checkpoint and sync the legacy WAL, then durably publish v10 with Mixed
data-page coverage. Cutover backs up only the header (32 KiB temporary WAL);
ordinary writes/checkpoints lazily checksum old pages. Byte 31 is 00 for legacy,
A5 for checksummed, and FF reserved for a future globally promoted file format.
Unknown markers fail closed. Only Mixed non-header pages at or below the
checksum-validated LegacyLastPageID may be legacy. Header bytes 160..164 store
coverage and the boundary; new/rebuilt files use Complete. Keep coverage Mixed
conservatively until explicit rebuild; there is no background migration.
Read-only legacy opens preserve their bytes. `Upgrade=true`
continues to rebuild v7 files before applying read-only access. Data checksums use
bytes 14..17 (the unused persisted transaction ID); WAL frames append 64 plaintext
metadata bytes and keep logical 8192-byte page addresses. Rotate the WAL salt only
after checkpointed data is durable and before recycling log positions. Never
rewrite an unconfirmed slot before the latest confirmation. Recovery validates
frame CRCs, transaction counts/digests, and commit sequence before publishing;
rebuild must use the same verifier. Flushes must reach the underlying file through
encryption, caller-stream, and checksum wrappers. Run
`python3 scripts/test-vector-compatibility.py` for legacy read-only compatibility,
automatic conversion, and old-engine rejection, including encrypted files.
See `docs/page-and-wal-checksums.md` and `docs/vector-query-compatibility.md`.
Run `LiteDB.Fuzz` targets `checksum-page,checksum-wal,checksum-migration,checksum-crash`
for format/recovery changes. Keep their pinned input/trace corpus replayable. The
short daily CI campaign uses three minutes per platform; longer local campaigns
can put artifacts under `/dev/shm` to avoid sustained physical disk writes.
Checksum oracles must verify complete document payloads and secondary-index
results, not merely successful open or row counts. CRC-valid malformed metadata
must be tested separately from random bit corruption.
Header overwrites require a synced WAL recovery footer; recover it before the
primary-header checksum gate, and sync repairs before removing the footer.
Conversion keeps legacy header redo: durably publish its intent before writing backup
pages, sync redo before preparation, and sync preparation before confirmation.
Incomplete encrypted redo can look confirmed, so never feed it to legacy replay
without checking the intent/preparation records. Keep read-only recovery byte
preserving. See `docs/header-publication.md` and the crash-boundary tests.

Checkpoint must verify the entire checksummed WAL and match its confirmed
transaction IDs to the live committed set before changing either file. Per-frame
validation during copying alone can certify a partial transaction before a later
read fails. Hold the exclusive lock across validation and copying, and retain
summaries rather than buffering all WAL page payloads.
The header journal binds the preceding WAL bytes. Before repairing or removing
it, verify that binding unless a validated newer header salt proves checkpoint
publication completed. Never discard damaged redo needed by partial data writes.
Keep the last nonempty recovery report across SharedEngine reopenings; distinguish
invalid/partial WAL tails from intact unconfirmed tails in `$database` diagnostics.

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
