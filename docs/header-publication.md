# Recoverable header publication

Format v10 uses the existing WAL to protect header replacement. This works with
files, caller-owned streams, and encryption; it does not require a sidecar file
or an atomic 8192-byte header write. A validated recovery copy is read before the
ordinary data-header checksum gate. Without such a copy, header corruption still
fails explicitly rather than being certified with a new checksum or salvaged
automatically.

## Checkpoint

1. Append the current, checksum-validated data header and a descriptor to the WAL.
   This temporary footer is 16384 bytes, encrypted through the same stream as the
   WAL. Sync it together with the preceding WAL before writing any data pages.
2. Copy committed WAL pages to data and sync data.
3. Publish and sync the new generation salt in the data header.
4. Truncate the WAL. A surviving old WAL cannot match the new salt.

If a crash tears the header in steps 2 or 3, opening validates the footer and uses
its intact header to bootstrap ordinary WAL recovery. A writable open writes and
syncs that header before removing the footer; the redo frames remain available.
If the primary v10 header is already valid, it takes precedence over the recovery
copy, including when its newer salt proves checkpoint completed. A read-only open
uses the recovered header in memory and preserves both files exactly.

The footer is excluded from logical WAL lengths and transaction verification.
Appending transaction pages while a footer is active is forbidden. A failed
checkpoint closes the engine; a failed repair preserves the recovery record for
another open. Semantic-error marker writes also retain a header recovery copy.
Rebuild's reader uses the same journal selection and WAL verification rules.

## Automatic v8/v9 conversion

Encryption makes even an in-place checksum-field update capable of damaging
other page-header fields if a ciphertext block tears. Conversion therefore keeps
temporary **legacy redo for every allocated page**, not just a header copy. The
redo occupies approximately the allocated database size plus 24 KiB, excluding
the encryption preamble. Unallocated preallocation is not copied. Documents and
indexes are not rebuilt, and no permanent backup file is created.

The conversion protocol is:

1. Start an unconfirmed legacy-shaped intent page. Write and sync its first
   32 bytes before extending it; a torn initial write is shorter than a legacy
   page. Write and sync the remainder, containing the original header and an
   independent CRC. No database page has changed yet.
2. Write unconfirmed legacy redo pages and a final header page, then sync them.
3. Append and sync a prepared header copy, independently checksummed and binding
   a CRC of the entire redo prefix.
4. Append and sync the footer descriptor, which is also a legacy header
   confirmation. Its checksum covers both footer pages. Confirmation cannot
   precede the durable redo and prepared copy.
5. Write data-page checksums and sync data. Publish and sync the v10 header with
   its generation salt. Only then truncate and sync the temporary WAL.

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
| 152 | 4 | CRC32C of the legacy redo prefix; zero for a v10 checkpoint |

Conversion's prepared copy uses `LDBPREP1` at offset 132 and a page-local CRC at
148, plus the redo position and CRC at the same offsets. The intent uses
`LDBEGIN1`, position zero, and its own page-local CRC. These offsets are reserved
header bytes, outside pragmas and the collection map. All checksums cover
plaintext; the stream encrypts complete AES blocks afterward.

Automatic conversion requires successful durable WAL flushes and fails before
editing data if the backup cannot be made durable. Existing v10 commit/checkpoint
fallback for storage that rejects sync remains reported by
`$database.durableLogFlush=false`; that mode cannot promise power-loss durability.
Successful syncs must actually persist the bytes. Independent damage to both the
primary data and its durable recovery copies can still require restore or salvage.
