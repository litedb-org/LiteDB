# Recoverable header publication

Format v10 uses the existing WAL to protect header replacement. This works with
files, caller-owned streams, and encryption; it does not require a sidecar file
or an atomic 8192-byte header write. A validated recovery copy is read before the
ordinary data-header checksum gate. Without such a copy, header corruption still
fails explicitly rather than being certified with a new checksum or salvaged
automatically.

## Checkpoint

Before changing either file, a checksummed checkpoint runs the same complete-WAL
transaction verifier as startup recovery and requires its confirmed transaction
IDs to match the live committed set. This catches a missing confirmation or stale
safepoint frames even when individual CRCs still verify. Failure stops the engine
and leaves both files unchanged for subsequent recovery. The exclusive lock
prevents engine writers from changing the WAL between validation and copying.
The extra pass keeps transaction summaries rather than buffering entire page
payloads; copying still verifies each frame's checksum.

1. Append the current, checksum-validated data header and a descriptor to the WAL.
   This temporary footer is 16384 bytes, encrypted through the same stream as the
   WAL. The descriptor also binds a CRC of the complete preceding WAL. Sync it
   together with the preceding WAL before writing any data pages.
2. Copy committed WAL pages to data and sync data.
3. Publish and sync the new generation salt in the data header.
4. Truncate the WAL. A surviving old WAL cannot match the new salt.

If a crash tears the header in steps 2 or 3, opening validates the footer and uses
its intact header to bootstrap ordinary WAL recovery. A writable open writes and
syncs that header before removing the footer; the redo frames remain available.
If the primary v10 header is already valid, it takes precedence over the recovery
copy, including when its newer salt proves checkpoint completed. A read-only open
uses the recovered header in memory and preserves both files exactly.

Before repairing a header or removing a journal, recovery verifies the bound WAL
CRC whenever the selected header still has the journal's generation salt. Damage
to that redo after data copying begins fails the open without changing either
source; discarding a damaged transaction at that point could leave its copied
pages in the database. A valid newer salt proves data publication completed, so
obsolete redo need not remain intact. Rebuild uses the same check and reports
failure rather than presenting partially checkpointed data as a healthy rebuild.

The footer is excluded from logical WAL lengths and transaction verification.
Computing the bound CRC validates those exact frames and transactions again and
matches the live confirmation end/sequence. This prevents damage appearing after
preflight from being sealed as authoritative redo by a separate unchecked read.
Appending transaction pages while a footer is active is forbidden. A failed
checkpoint closes the engine; a failed repair preserves the recovery record for
another open. Semantic-error marker writes also retain a header recovery copy.
Rebuild's reader uses the same journal selection and WAL verification rules.

## Automatic v8/v9 conversion

After recovering/checkpointing the legacy WAL and syncing both files, cutover
only overwrites page zero. Other allocated pages keep their legacy representation
until ordinary writes checkpoint them. The temporary legacy header redo and
journal occupy **32 KiB**, excluding the encryption preamble, independent of the
data-file size. Documents and indexes are not rebuilt, and no permanent backup
file is created. Draining a nonempty legacy WAL still costs its normal checkpoint.

The conversion protocol is:

1. Start an unconfirmed legacy-shaped intent page. Write and sync its first
   32 bytes before extending it; a torn initial write is shorter than a legacy
   page. Write and sync the remainder, containing the original header and an
   independent CRC. No database page has changed yet.
2. Write and sync one unconfirmed legacy header redo page.
3. Append and sync a prepared header copy, independently checksummed and binding
   a CRC of the entire redo prefix.
4. Append and sync the footer descriptor, which is also a legacy header
   confirmation. Its checksum covers both footer pages. Confirmation cannot
   precede the durable redo and prepared copy.
5. Publish and sync the v10 header with its generation salt, `Mixed` coverage,
   and `LegacyLastPageID` from the checkpointed legacy header (`Complete` for an
   empty database). Only then truncate and sync the temporary WAL. Subsequent
   transactions use exclusively v10 WAL frames.

There is no in-place data-page rewrite during cutover, including with encryption.
Subsequent checkpoints use the normal verified v10 WAL and header journal when
converting touched pages; torn data/marker writes replay from that WAL.

An intact intent without a verified preparation means the backup was incomplete:
current readers ignore it and use the unchanged legacy data. This matters for
encrypted tears, whose random plaintext can resemble a confirmation bit or page
ID. The intent is accepted only while the primary legacy header still matches
its original contents, ignoring reserved journal metadata and the unused
transaction field.

A valid preparation proves the complete redo prefix even if the final
confirmation tears. Current readers validate that prefix and expose a logical
confirmation on its last header page, hiding the physical footer. Writable open
checkpoints this verified redo before retrying conversion; read-only open uses it
without modifying the images. Once the primary v10 header is valid, the old
conversion redo is ignored and can be removed after syncing that header.

These records retain legacy page layouts until v10 publication. The compatibility
test checks that LiteDB 5.0.21 can read, write, and checkpoint an interrupted
conversion before publication, and that the current engine then preserves those
writes on conversion, with or without an intervening legacy checkpoint. If older
engines append commits after the completed footer, current recovery locates that
footer from the intent and replays the subsequent legacy transactions too.
Older engines still use their own legacy recovery rules;
use the current engine to recover torn conversion records. After publication,
the v10 format boundary prevents old engines from reading checksummed WAL frames.

## Encoding and durability limits

Footer descriptor offsets, relative to its second 8192-byte page, are:

| Offset | Size | Field |
| --- | --- | --- |
| 132 | 8 | `LDBJRNL1` magic, little-endian |
| 140 | 8 | Byte position of the footer in the decrypted WAL |
| 148 | 4 | CRC32C of both pages, with this field zeroed |
| 152 | 4 | CRC32C of the entire WAL prefix preceding the footer |

Conversion's prepared copy uses `LDBPREP1` at offset 132 and a page-local CRC at
148, plus the redo position and CRC at the same offsets. The intent uses
`LDBEGIN2`, position zero, and its own page-local CRC. Its completed footer
starts at 16384 bytes, after intent and header redo. The reader also recognizes
the earlier draft `LDBEGIN1` intent and its page-count-derived footer position. These offsets are reserved
header bytes, outside pragmas and the collection map. All checksums cover
plaintext; the stream encrypts complete AES blocks afterward.

Automatic conversion requires successful durable WAL flushes and fails before
editing data if the backup cannot be made durable. Existing v10 commit/checkpoint
fallback for storage that rejects sync remains reported by
`$database.durableLogFlush=false`; that mode cannot promise power-loss durability.
Successful syncs must actually persist the bytes. Independent damage to both the
primary data and its durable recovery copies can still require restore or salvage.

Writes also require power-safe overwrite: bytes outside the addressed write range
that were previously synced must remain intact. The 8256-byte WAL frames can share
512/4096-byte physical sectors. Sector-boundary tests lose new bytes in a shared
sector while preserving the acknowledged prefix and allowing later confirmation
sectors to persist. Storage that tears previously synced neighboring bytes outside
the write range violates this assumption and can lose acknowledged commits; the
checksums do not replace device power-loss protection.
