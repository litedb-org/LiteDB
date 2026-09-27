# Data-page and WAL checksums (#2935)

Format **10** introduced complete data-page checksum coverage. New databases
use **11** for BSON-only creation or **12** for Auto compact creation,
retaining the index-ordering contract and checksum encoding. The checksum-only conversion described below is followed by index
migration: that additional work can rewrite indexes and use significant WAL/temp
space. Legacy read-only opens that require index migration fail without changing
the files. See [index migration](collation-runtime-compatibility.md).
Writable opens of formats 8 and 9 automatically recover/checkpoint their legacy
WAL, sync both files, and durably publish v10 with **mixed** data-page coverage.
Cutover overwrites only the header and uses **32 KiB** of temporary WAL (plus the
8 KiB encryption preamble when encrypted), independent of allocated data size.
Untouched data pages stay legacy; ordinary writes/checkpoints checksum them lazily.
Draining an existing legacy WAL still requires its normal checkpoint work.
Documents and indexes are not rebuilt and no permanent backup is created.
Keep a backup if the file must remain usable by an older engine. Read-only legacy
opens do not convert or modify either file. The `Upgrade=true` v7 rebuild path
remains available and produces fully checksummed v10.

Older engines reject the v10 data header before reading the new WAL layout.
Legacy WALs retain the legacy recovery rules until conversion: checksums cannot
retroactively verify writes made by older engines. Unknown future formats are
rejected. Do not edit the version byte to bypass the format boundary.

## Data pages

Pages remain 8192 bytes, with unchanged payload and index layouts. Byte 31 is the
page-format marker:

| Marker | Meaning |
| --- | --- |
| `00` | Legacy; allowed only for non-header pages at or below `LegacyLastPageID` while coverage is `Mixed` |
| `A5` | Checksummed v1; CRC validation is mandatory |
| `FF` | Reserved extended page format; rejected by this engine |
| All other values | Unknown; rejected |

Legacy and checksummed markers differ in four bits. A single-bit change cannot
downgrade a checksummed page to legacy, and every single-bit change of either
marker fails closed. This is not protection against deliberate edits or arbitrary
multi-bit corruption that recreates a valid legacy marker. Future engines must
durably promote the global file version before writing an extended page format;
reserving `FF` does not authorize v10 writers to use it.

For checksummed data pages, bytes 14–17 (the old persisted transaction ID) hold a
little-endian CRC32C (Castagnoli). The CRC covers the entire plaintext page,
including the marker, with those four checksum bytes treated as zero. Reads
validate the CRC and page ID before parsing. Legacy pages retain their historical
transaction field and still undergo page-ID validation. Every v10 WAL payload
uses `A5`, but retains its live transaction ID and relies on the frame CRC.
Checkpoint stamps the data CRC when writing that payload to the data file.

The database header is always checksummed. It reserves bytes 109–124 for a random
128-bit WAL-generation salt and bytes 125–128 for the `CRC1` marker, checked
independently of the version byte. Byte 160 holds coverage (`A5` = Mixed,
`5A` = Complete); bytes 161–164 hold little-endian `LegacyLastPageID`, zero for
Complete. These permissions come only from a checksum-validated header. New
pages beyond the legacy boundary always require checksums, including pages
allocated from previously unused preallocation. Reused legacy page IDs are stamped
with checksums on write. Rollback does not migrate data pages.

`$database.checksums` identifies the v10 checksum/WAL format; it does **not** imply
all old data pages are protected. `$database.checksumCoverage` reports `Legacy`,
`Mixed`, or `Complete`, and `$database.legacyLastPageID` reports the old-page bound.
Coverage remains conservatively Mixed even if normal writes have eventually
converted every old page. There is no background scan or migration cursor in
this change. An explicit `Rebuild()` produces Complete coverage, after which any
legacy marker is corruption. Until then, untouched legacy payload damage has the
same detection limits as the legacy format.

Data checksum failures raise `LiteException.CHECKSUM_MISMATCH` (140), including the
file origin and byte position, and stop an active engine. They are not silently
repaired or marked for automatic rebuild. Explicit rebuild validates pages and
records unreadable pages as salvage errors. Checksums detect damage; they do not
restore data when no intact copy exists.

## WAL frames

Every logical WAL page is stored as 8192 plaintext page bytes followed by a 64-byte
trailer (8256 physical bytes, less than 0.8% space overhead). Engine page addresses
remain logical multiples of 8192. Trailer offsets and integer encodings are:

| Offset | Size | Meaning |
| --- | --- | --- |
| 0 | 4 | `WAL1` magic |
| 4 | 4 | CRC32C of page and trailer, with this field zeroed |
| 8 | 16 | Generation salt copied from the durable data header |
| 24 | 8 | Logical WAL position, little-endian |
| 32 | 4 | Transaction's distinct physical-slot count |
| 36 | 8 | Transaction digest |
| 44 | 8 | Commit sequence, or zero for an unconfirmed frame |
| 52 | 12 | Reserved zeros, included in the frame CRC |

The page retains its transaction ID and confirmation flag. Each slot contributes
a position-dependent nonlinear 64-bit mix of its page CRC and generation salt to
the transaction digest. Digests XOR these contributions, allowing a safepoint to
replace a slot's contribution without rereading its page. The nonlinear mix avoids
the cancellation of identical bit changes that occurs with a simple XOR of CRCs.
The confirm frame includes itself in its final count and digest. Commit sequences
are consecutive within a generation, detecting a lost confirmation even if its
slot instead contains an intact unconfirmed frame.

Safepoints can reuse only unconfirmed slots **after** the latest confirmation.
Earlier slots are frozen: rewriting them could tear an already-synced WAL prefix
and make recovery discard an acknowledged intervening transaction. Rollback
releases its checksum bookkeeping. Confirmations always append.

Checksums are computed before encryption. The trailer and page are encrypted
together using complete AES blocks; encryption and caller-stream wrappers forward
durable flushes to the underlying file. CRC32C uses hardware instructions on
supported .NET runtimes and a tested slicing-by-eight fallback elsewhere. These
are accidental-corruption checks, not cryptographic authentication.

## Recovery and checkpoint ordering

Recovery validates every frame's CRC, salt, and position. It publishes a
transaction only when its confirmation's page count, digest, and commit sequence
match the observed frames. At the first invalid frame or confirmation it stops,
retains the last verified committed prefix, and discards the rest, including later
transactions that might depend on the missing transaction. Partial final frames
and uncommitted trailing frames are also removed. A writable open truncates and
syncs the tail; read-only recovery exposes the same prefix without changing bytes.
Operational I/O exceptions propagate and never trigger checksum-tail truncation.
Explicit rebuild uses the same verifier instead of trusting confirmation bits.

`$database.recoveryDiscardedWalBytes` reports bytes excluded by the last recovery
with a nonempty tail (including incomplete or uncommitted tails).
`$database.recoveryInvalidWalTail` distinguishes a failed integrity check or
partial frame from an intact, unconfirmed tail. It cannot determine whether a
damaged frame belonged to an acknowledged commit. Shared mode retains this
report across its internal engine reopenings for the lifetime of that
`LiteDatabase`/`SharedEngine` instance. A new independent owner starts a new report;
it is not persisted or a process-wide history.

Checkpoint first validates the entire checksummed WAL and its committed
transaction set before changing either file. It then appends a 16 KiB header recovery record and syncs the WAL before
copying pages, syncs all data pages, then writes
and syncs a fresh generation salt in the data header before truncating the WAL.
The recovery record binds a CRC of the preceding WAL. If that redo changes after
data overwrites start, reopening fails before repair or truncation, preserving
both sources instead of exposing a partial checkpoint as healthy data. A valid
header with a newer salt proves publication completed and allows obsolete WAL
to be ignored. Stale frames left behind by a crash during truncation cannot match that salt.
Torn headers bootstrap recovery from that verified record. Conversion retains
legacy redo and an independently verified preparation so torn ciphertext or a
torn confirmation cannot publish a partial backup. Read-only recovery preserves
both files. See the [header-publication protocol](header-publication.md) for
ordering, encoding, temporary space, and durability limits.

With `DurableCommits=false`, power loss may lose recent commits, but a partially
present transaction is not recovered. Storage must still honor successful syncs
for the usual durable-commit and checkpoint guarantees. Log storage that rejects
sync as unsupported (#2242) converts and checkpoints in write order without the
log sync: a killed process still recovers, power loss is not covered. A failed
sync still stops before data is overwritten. On Unix the file sync is issued natively
(`fsync`, or `F_FULLFSYNC` on macOS), because `FileStream.Flush(true)` in released .NET
runtimes reports success for every failed `fsync`.

## Validation

`WalChecksum_Tests` checks missing, torn, reordered, removed, partial, and stale
frames, stale reused slots, confirmation counts, commit-sequence gaps, read-only
recovery, truncation, subsequent writes, and a second open after checkpoint.
`WalPowerLoss_Tests` uses a storage double that persists confirmation while losing
a middle frame, both with opted-out commits and an interrupted durable commit.
`LazyChecksumCutover_Tests` proves bounded header I/O and 32 KiB WAL use on a
500 GiB sparse model. `LazyChecksumMigration_Tests`, `LazyChecksumMarker_Tests`,
and `LazyChecksumCheckpoint_Tests` cover mixed reads, out-of-order conversion,
marker corruption/extension rejection, rollback/page reuse, torn mixed checkpoints,
and explicit rebuild completion, plain and encrypted.
`PageChecksum_Tests` covers known CRC vectors, portable/hardware agreement,
plain/encrypted data damage, legacy WAL recovery, and v8/v9 conversion.
Existing transaction-boundary, slot-reuse, flush-failure, and vector suites cover
interleaved transactions, rollback, ownership, and format monotonicity.
`ChecksumCheckpointCrash_Tests`, `ConversionJournalCrash_Tests`, and
`HeaderJournalFailure_Tests` cover torn headers, every conversion redo write,
failed backup/repair syncs, corrupted recovery copies, journal cleanup, and
file-backed recovery. See the [merge-review test matrix](checksum-merge-review.md).

Run `python3 scripts/test-vector-compatibility.py` to check against NuGet LiteDB
5.0.21 in separate processes. Run `dotnet run --project tools/WalChecksumBenchmarks
-c Release` for warmed single-insert and InsertBulk measurements with production
assemblies, including encrypted files.

### Measurements

Measured on Linux x64 with .NET 8 production assemblies (`TestingEnabled=false`),
baseline `ffc3edfc9`, one warmup and five samples per case. Each document has a
256-character payload; single-insert cases write 1000 documents, and InsertBulk
cases write 10000. Automatic checkpoints are disabled during timing. Tiered
compilation was disabled for the repeated comparison:

```sh
DOTNET_TieredCompilation=0 dotnet run --project tools/WalChecksumBenchmarks -c Release
DOTNET_TieredCompilation=0 dotnet run --project tools/WalChecksumBenchmarks -c Release -- --memory
```

| Storage | Encryption | Operation | Before median (ms) | After median (ms) |
| --- | --- | --- | ---: | ---: |
| File, durable commits | No | 1000 inserts | 5183.71 | 5151.28 |
| File, durable commits | Yes | 1000 inserts | 5254.66 | 5228.41 |
| File, durable commits | No | InsertBulk 10000 | 114.20 | 95.18 |
| File, durable commits | Yes | InsertBulk 10000 | 107.57 | 94.41 |
| Memory | No | 1000 inserts | 41.55 | 43.99 |
| Memory | Yes | 1000 inserts | 70.53 | 55.81 |
| Memory | No | InsertBulk 10000 | 99.42 | 84.77 |
| Memory | Yes | InsertBulk 10000 | 103.53 | 81.99 |

Durable single-insert medians differed by less than 1%. The plain memory case
added approximately 2.4 microseconds per insert. This was a shared host with
substantial scheduling/device variability (the first baseline file run was about
9.2 seconds per 1000 inserts); lower times in other cases are not evidence of a
checksum-related speedup. These numbers exclude one-time legacy conversion and
checkpoint's additional generation-header sync.


### Lazy cutover cost

A paired production comparison against `69db46c9c` used the same 10000-document,
approximately 4.4 MiB fixture, one warmup and five samples, with tiered compilation
disabled. Legacy fixture creation (including resetting byte 31) was outside timing.

| Encryption | Operation | Eager baseline median (ms) | Lazy median (ms) |
| --- | --- | ---: | ---: |
| No | Automatic conversion on open | 53.39 | 40.88 |
| Yes | Automatic conversion on open | 77.91 | 58.55 |
| No | Checkpoint | 27.09 | 26.37 |
| Yes | Checkpoint | 32.96 | 32.17 |

The fixed durability barriers still dominate this small fixture. These timings
are not an estimate for a 500 GiB database. The sparse-stream test proves bounded
cutover I/O without allocating or writing 500 GiB: any access past the physically
stored header throws, while the stream reports that large logical length.
Draining a nonempty legacy WAL remains proportional to its checkpoint workload.

### Earlier eager-conversion measurements (superseded)

The following historical comparison predates lazy conversion; its conversion
cost and temporary-space requirement do not describe the current implementation.
It isolates the header-recovery protocol against checksum
implementation `9c87328e6`, before that protocol was added. It uses durable files,
10000 documents with 256-character payloads (approximately 4.4 MiB allocated),
one warmup and five samples on the same Linux x64 host with production assemblies.
Conversion changes the legacy metadata in place without rebuilding documents.

```sh
DOTNET_TieredCompilation=0 dotnet run --project tools/WalChecksumBenchmarks -c Release -- --maintenance
```

| Encryption | Operation | Before median (ms) | With recovery journal (ms) |
| --- | --- | ---: | ---: |
| No | Checkpoint | 25.99 | 36.76 |
| Yes | Checkpoint | 30.28 | 41.29 |
| No | Automatic conversion on open | 19.58 | 89.40 |
| Yes | Automatic conversion on open | 44.81 | 119.91 |

That implementation wrote and synced a 16 KiB recovery footer even when the preceding
WAL was already synced. Its eager conversion wrote complete legacy redo and used
additional durability barriers before changing data. These shared-host samples
show that cost; they are not a throughput or latency guarantee. That conversion also
required temporary WAL space approximately equal to allocated data plus 24 KiB.

The additional checkpoint preflight was measured separately against `187fac92e`
(header journaling already enabled), with the same maintenance fixture and
production settings:

| Encryption | Checkpoint before preflight (ms) | Checkpoint with preflight (ms) |
| --- | ---: | ---: |
| No | 23.55 | 25.17 |
| Yes | 27.93 | 31.21 |

The extra full WAL read and transaction verification added approximately 1.6 ms
plain and 3.3 ms encrypted in this paired run. Absolute timings varied from the
earlier header-journal run; compare within each table rather than combining
medians from separate runs. Preflight retains transaction summaries, not all page
payloads, and leaves both files unchanged if the live committed set cannot verify.

Binding verified WAL bytes into the checkpoint journal adds another streaming
verification pass. A paired comparison against `295666869`, using the same fixture
and production settings, measured plain checkpoint medians of **24.84 → 27.43 ms**
and encrypted medians of **30.24 → 32.89 ms**. The additional protection cost about
2.6 ms for this 4.4 MiB fixture. Conversion medians were 56.04 → 55.26 ms plain and
77.91 → 80.46 ms encrypted; that binding change did not alter the then-eager conversion protocol.

## Released-engine WAL length compatibility

LiteDB 5.0.21 rounds the physical WAL length down to an 8 KiB boundary before
checking the database version, even for read-only open. Flushed checksummed WALs
therefore retain less than 8 KiB of trailing padding. Logical pages still map to
8,256-byte frames; padding is not a frame and is overwritten by the next append.
Recovery also accepts older unpadded checksummed files. Padding does not add
per-page overhead beyond the 64-byte trailer.

The temporary header journal follows aligned padding and includes those bytes
in its WAL binding. Its footer remains aligned too. Before sealing the journal,
padding is written through encryption as plaintext zeroes so the binding is
independent of encrypted blank-page normalization and read chunk sizes. Old
unaligned journal footers remain readable. The format boundary still rejects
old engines; padding prevents their preliminary length check from truncating
acknowledged frames or a sealed recovery footer before that rejection.
