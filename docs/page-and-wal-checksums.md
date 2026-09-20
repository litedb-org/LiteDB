# Data-page and WAL checksums (#2935)

New databases use format **10**. Writable opens of formats 8 and 9 automatically
recover their legacy WAL, checkpoint it, and add checksums to existing pages.
The page checksums are synced before the v10 header is written and synced. This
is an in-place metadata conversion, with work proportional to the allocated
database pages. Temporary legacy redo and a header journal protect interrupted
conversion; allow approximately the allocated file size plus 24 KiB of WAL space.
Documents and indexes are not rebuilt and no permanent backup is created.
Keep a backup if the file must remain usable by an older engine. Read-only legacy
opens do not convert or modify either file. The `Upgrade=true` v7 rebuild path
remains available and produces v10.

Older engines reject the v10 data header before reading the new WAL layout.
Legacy WALs retain the legacy recovery rules until conversion: checksums cannot
retroactively verify writes made by older engines. Unknown future formats are
rejected. Do not edit the version byte to bypass the format boundary.

## Data pages

Pages remain 8192 bytes, with unchanged payload and index layouts. Bytes 14–17,
formerly the unused transaction ID in checkpointed data pages, hold a little-endian
CRC32C (Castagnoli). The CRC covers the entire plaintext page with those four bytes
treated as zero. Reads validate the CRC and page ID before parsing page contents.
Checkpoint, initial creation, conversion, recovery-marker writes, and WAL-salt
rotation all regenerate the data checksum.

The database header reserves bytes 109–124 for a random 128-bit WAL-generation
salt and bytes 125–128 for the `CRC1` marker. The marker is checked independently
of the version byte, so a damaged version byte cannot silently disable validation.
Both fields are covered by the header checksum. Unallocated preallocation beyond
`LastPageID` remains zero-filled; it receives checksums when real pages are written.

Data checksum failures raise `LiteException.CHECKSUM_MISMATCH` (139), including the
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

`$database.checksums` reports whether checksums are enabled.
`$database.recoveryDiscardedWalBytes` reports bytes excluded during this open
(including incomplete or uncommitted tails); recovery also emits a `RECOVERY` log
message. The diagnostic is per-open, not persisted.

Checkpoint first validates the entire checksummed WAL and its committed
transaction set before changing either file. It then appends a 16 KiB header recovery record and syncs the WAL before
copying pages, syncs all data pages, then writes
and syncs a fresh generation salt in the data header before truncating the WAL.
Stale frames left behind by a crash during truncation cannot match that salt.
Torn headers bootstrap recovery from that verified record. Conversion retains
legacy redo and an independently verified preparation so torn ciphertext or a
torn confirmation cannot publish a partial backup. Read-only recovery preserves
both files. See the [header-publication protocol](header-publication.md) for
ordering, encoding, temporary space, and durability limits.

With `DurableCommits=false`, power loss may lose recent commits, but a partially
present transaction is not recovered. Storage must still honor successful syncs
for the usual durable-commit and checkpoint guarantees.

## Validation

`WalChecksum_Tests` checks missing, torn, reordered, removed, partial, and stale
frames, stale reused slots, confirmation counts, commit-sequence gaps, read-only
recovery, truncation, subsequent writes, and a second open after checkpoint.
`WalPowerLoss_Tests` uses a storage double that persists confirmation while losing
a middle frame, both with opted-out commits and an interrupted durable commit.
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


### Header recovery maintenance cost

The following comparison isolates the header-recovery protocol against checksum
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

Checkpoint now writes and syncs a 16 KiB recovery footer even when the preceding
WAL was already synced. One-time conversion writes complete legacy redo and uses
additional durability barriers before changing data. These shared-host samples
show that cost; they are not a throughput or latency guarantee. Conversion also
requires temporary WAL space approximately equal to allocated data plus 24 KiB.

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
