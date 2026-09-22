# WAL retirement witnesses (file format v13)

The v10 page CRC and WAL framing remain intact: 8,192 logical bytes plus 64 trailer
bytes. A confirmation's physical logical offset / 8192 + 1 identifies its snapshot
version. Confirmations always append; no witness record is itself a transaction
confirmation. Reuse never moves a retained payload. Flushed WALs and sealed
header journals are physically padded to an 8 KiB boundary to prevent released
engines from truncating them before version rejection; see
[WAL padding](page-and-wal-checksums.md#released-engine-wal-length-compatibility).

## Header root

The data-header checksum covers:

| Offset | Bytes | Meaning |
| --- | --- | --- |
| 168 | 8 | Latest witness record's logical offset + 8192; zero means no chain |
| 176 | 4 | CRC32C of that entire 8,256-byte plaintext frame |
| 180 | 8 | Minimum confirmed commit sequence required by this root |

A zero root requires zero CRC and sequence. A nonzero root requires v13 and a
positive sequence. Loading verifies the entire backward chain before permitting
any retired payload to be absent. A truncated, cyclic, mismatched or malformed
chain is corruption, not an ignorable unconfirmed WAL tail.

## Witness frame

The ordinary WAL trailer retains its magic, CRC, generation salt and original
logical position. Offset 52 of the trailer is `RET1` (`0x31544552`); its normal
transaction count, digest and sequence fields are zero. The page has the checked
page marker, with zero page type, ID, transaction ID and confirmation flag.

| Page offset | Bytes | Meaning |
| --- | --- | --- |
| 32 | 4 | `RET1` |
| 36 | 8 | Previous witness record offset + 8192; zero terminates the chain |
| 44 | 4 | CRC32C of the previous complete physical frame |
| 48 | 4 | Entry count, 1–145 |
| 52 | 8 | Confirmed sequence at retirement preparation |
| 64 | 56 each | Witness entries |

Every link points backward. A record cannot witness another record. Its original
slot must precede the record. Duplicate witnesses for the same position and
transaction ID are invalid. A position can have witnesses for several different
transactions after repeated reuse. Record pages are never reused.

| Entry offset | Bytes | Original frame field |
| --- | --- | --- |
| 0 | 8 | Logical position |
| 8 | 4 | Page ID |
| 12 | 4 | Transaction ID |
| 16 | 1 | Page type |
| 17 | 1 | Confirmation flag |
| 24 | 8 | Position/salt-dependent contribution to transaction digest |
| 32 | 4 | Frame count |
| 36 | 8 | Transaction digest |
| 44 | 8 | Commit sequence, zero for nonconfirmation frames |

Unused entry bytes are reserved. Proofs retain metadata, not old page payloads.
The root CRC and each backward-link CRC bind the precise witness bytes; frame CRCs
also bind salt and physical placement.

## Recovery

At each physical slot, replay its witnesses before considering its current bytes.
Witnesses contribute to the original transaction count/digest and can supply its
original confirmation. They never enter the page index as readable payloads.
An intact old incarnation matching a witness is ignored. A damaged incarnation
in a witnessed slot can be an interrupted unconfirmed reuse and is omitted; any
later confirmation still needs its contribution, count and sequence. Unwitnessed
damage uses normal WAL corruption handling.

The transaction verifier processes the resulting logical stream. It still
requires every frame contribution and an uninterrupted confirmation sequence.
Recovery must reach the header root's minimum sequence; otherwise it fails before
truncating a tail. It also retains all bytes through the root, even when the last
confirmation preceded the witness records. Rebuild uses the same logic.

Recovery rebuilds free capacity only for witnessed positions without surviving
committed payloads. A surviving unconfirmed incarnation can be replaced because
its transaction will never resume; its observed ID still advances the allocator.
Within a live transaction, repeated versions of one page remain in increasing
physical order so replay selects the last one after intervening reclamation.

## Publication and interrupted recovery

The checkpointer validates the WAL and live committed set while commit publication
and safepoints are excluded. On first use it durably publishes v13 through the
existing header journal before appending any witness representation. It then
writes/syncs witness records, seals/syncs a WAL-bound header journal, backfills and
syncs data, writes/syncs the root, removes/syncs the header journal, clears/syncs
obsolete payloads, and only then advertises reusable slots.

| Interruption | Recovery |
| --- | --- |
| Version promotion | Existing header journal selects a valid old/new header; retry is safe |
| Incomplete witness append | Old root remains authoritative; no payload has been cleared |
| Backfill or torn root header | WAL-bound journal protects repair; original payloads and earlier witnesses remain |
| Root durable, footer still present | Validate binding and root, then finish footer cleanup |
| Payload clearing or reuse | Published witnesses preserve older proofs; incomplete new transactions remain unconfirmed |
| Full checkpoint/salt reset | Data is synced before root removal and generation rotation; the existing journal protects header tears |

Repeated repair must sync its selected recovery journal before writing the primary
header. Read-only recovery makes no writes. Required metadata or redo corruption
fails closed; these records do not recover arbitrary independent damage to both
copies. The durability contract requires real durable sync and power-safe overwrite
outside the addressed write range. See [snapshot checkpointing](mvcc-checkpoint.md)
for caller-visible behavior and validation.
