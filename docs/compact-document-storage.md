# Compact document storage

Issue: https://github.com/litedb-org/LiteDB/issues/2920

`CompactStorageMode` controls writes in `EngineSettings`, `ConnectionString`, and
`RebuildOptions`. `Auto` is the default: new databases are created as v10 and all
databases use compact writes when beneficial. Existing v8/v9 databases are lazily
promoted on their first compact write. `Legacy` always writes BSON. `Compact`
also enables compact writes, but unlike `Auto` a new empty database remains v8
until it actually stores a beneficial compact document.
Connection strings use `Compact Storage=Auto|Legacy|Compact`; the earlier Boolean
spellings remain accepted as aliases for `Compact` and `Legacy`.

All modes read BSON and compact documents. Changing the mode does not scan or
rewrite existing documents. Read-only and idle opens of existing databases do not
promote the file. Public `BsonSerializer` continues to produce BSON.

New `Auto` databases start at v10 so later opens can retain their write policy.
For an existing v8/v9 database opened with `Auto` or `Compact`, the first
beneficial compact write durably promotes the file to v10 before schema or document pages can reach
the WAL. `Legacy` files remain v8 unless vectors require v9, and promotion is
monotonic through rollback, replay, and checkpoint. A rolled-back first compact
write can leave a v10 file containing only BSON.
Older engines reject v10; simultaneous writable access by different engine
versions is not supported.

### Power-loss recovery during promotion

Opening an intact legacy database does not perform a compact upgrade or rewrite
its documents. The first compact write reads the 8 KiB persisted header, changes
only its version, and first appends a SHA-256-checked recovery image in two 8 KiB
WAL padding pages. It durably flushes the WAL (and its directory entry on Unix)
before overwriting and durably flushing the data header. This is a fixed 16 KiB
WAL append plus one 8 KiB header rewrite, independent of database size, with two
durability barriers. Schema/document pages use the normal transaction afterward.

Startup validates the recovery image before validating the data header, so a
torn header write can be repaired. A writable open re-syncs the recovery image
before repair; a read-only open uses it in memory. Committed WAL headers are then
replayed normally, retaining collections and allocations newer than the persisted
header. Recovery is repeatable if power fails again. The record remains until
checkpoint has synced the data; WAL truncation is then synced before slots can be
reused. That retirement adds a one-time WAL flush. Encrypted records retain the
raw blank-page sentinel, so even a torn record cannot become a transaction page;
the recovery payload remains encrypted. Both v9 vector and v10 compact promotion
use this protocol. Normal transaction replay ignores the padding records.

These guarantees assume storage honors successful durable flushes and the data
file and its WAL are retained together. Caller-owned streams must provide their
own persistence contract; ordinary MemoryStream.Flush cannot model real disk
durability. This does not strengthen the existing transaction guarantees when
`DurableCommits=false`, nor protect against media failure that destroys already
flushed data. The compact power-loss tests and `compact-power-loss` fuzzer cover
promotion, journal/header tears, interrupted recovery, WAL/checkpoint cut points,
read-only opens, and subsequent WAL reuse for plain and encrypted v8/v9 files.

The regression evidence is organized by invariant:

| Invariant | Discriminating coverage |
| --- | --- |
| No header overwrite without durable recovery bytes | Cuts and short/torn journal writes before the first promotion flush |
| Torn header is recoverable | Byte-boundary and damaged-suffix header writes, plain and encrypted |
| Recovery remains recoverable | Repeated repair cuts and torn repairs using initially OS-cached journals |
| Existing committed state is retained | Independent document/index model, a WAL-only collection, and sequential v9/v10 promotion |
| Old journal cannot reappear in a new WAL epoch | Checkpoint/retirement cuts followed by new writes and another restart |
| Recovery does not mutate read-only or unrelated files | Real-file recovery snapshots, byte comparisons, and unrelated-file sentinel |
| Invalid recovery bytes cannot authorize repair | Damaged image/checksum rejection with unchanged files |

These cases live in `CompactPromotionPowerLoss_Tests` and
`CompactPromotionFileRecovery_Tests`; the shared fault model also drives the
`compact-power-loss` fuzzer. Hosted PR checks exercise the current revision on
Windows, Linux, and macOS. Compatibility scripts additionally use released
LiteDB 5.0.21 processes against real data/WAL files.

`Rebuild(new RebuildOptions { CompactStorage = CompactStorageMode.Compact })`
converts useful shapes and regenerates catalogs through the existing temporary-
file/backup process. `Legacy` explicitly writes BSON to the rebuilt file; vector
data/indexes still impose the v9 floor. A null rebuild option retains the engine
policy. `Auto` rebuilds into a new v10 database and uses compact writes; select
`Legacy` explicitly to retain or restore the old format. The setting on the
existing engine remains unchanged after rebuild;
reopen with `Legacy` before using a downgraded file with old software. Explicit
rebuild options retain the existing password/collation semantics.

## V1 layout

All integers are little-endian. DataBlock layout is unchanged. Root payloads are:

| Field | Bytes |
| --- | ---: |
| Magic `0xC14C4442` (negative Int32, never a legal BSON length) | 4 |
| Codec version `1` | 1 |
| Reserved flags `0` | 1 |
| Total encoded length, including header | 4 |
| Encoded document | variable |

Every encoded document begins with a UInt32 schema ID. ID zero means inline
fields: UInt16 count followed by (UInt16 UTF-8 name byte length, name bytes,
typed value) pairs. A nonzero ID is followed by the UInt64 schema fingerprint,
a presence bitmap of `ceil(schema fields / 8)` bytes (least-significant bit
first), then typed values for the present slots in schema order. Unused bitmap
bits must be zero. Inline documents support arbitrary nested shapes without
allocating catalog entries.

Values retain a one-byte LiteDB `BsonType` tag. Arrays use Int32 count followed
by typed values, without BSON's numeric CString keys. Documents recurse into
the document layout. Strings/binary use Int32 byte length and bytes. Guid and
ObjectId retain the public serializer's byte order. Decimal uses four Int32
parts, dates use BSON milliseconds and its min/max sentinels, and vectors use
UInt16 dimensions plus Single components. Other scalars retain their existing
width. Missing slots differ from present Null values. Field order and casing
are preserved, including `GetElements()`'s `_id`-first convention.

Collection bytes 52–67 hold `LDC1` magic, UInt32 root page, UInt32 tail page,
and UInt32 schema count. Reserved bytes in v8/v9 collections have no meaning
without this explicit marker. A Schema page (page type 6) has the normal page
header and links; byte 32 starts `SCH1`, UInt16 used payload bytes, UInt16 entry
count, and entries. An entry is UInt32 ID, UInt64 fingerprint, UInt16 field count,
and length-prefixed UTF-8 names. The fingerprint is FNV-1a-64 over the ordered,
length-prefixed definition beginning at field count. It detects mapping damage;
it is not an adversarial checksum or a checksum of document values.

## Admission and limits

An existing schema can represent an ordered, case-exact subsequence, so optional
fields reuse a superset and changing value types does not create schemas. A new
shape must be observed in two documents before admission (updating the same
document does not count as a second observation). Candidate history is only a hint:
collisions may admit a shape sooner but can never assign values to other names.
Document identity hashes can conservatively delay admission on collisions.
Candidates are bounded and survive individual transactions within one engine;
a fresh SharedEngine operation may need a bulk write to establish a new schema.

Limits are 256 schemas per collection, 128 fields per schema, 512 UTF-8 bytes per
schema field, 4096 bytes per schema entry, and 64 levels of compact nesting.
Thus persisted definitions are bounded by 1 MiB per collection (plus page
packing overhead). Admission history is bounded to 2048 hashes per engine.
Documents below 64 BSON bytes skip compact preparation. Dynamic scalar shapes
with no reusable schema skip value encoding; after 16 unsuccessful attempts a
write snapshot skips 128 scalar documents before sampling again. Large scalar
documents whose field names account for less than 10% of their BSON size also
skip preparation. A compact result must save more
than eight payload bytes; otherwise writes remain BSON and no tentative schema
is persisted. Existing logical BSON and physical document-size limits both
apply. Unused committed schema IDs are never reassigned; rebuild regenerates
only definitions needed by live documents.

Schema definitions and document pages use ordinary snapshot visibility and WAL
transactions. Write snapshots own tentative catalog objects. Only read snapshots
populate the shared catalog cache, keyed by collection page and read version.
Checkpoint clears it before read-version numbers reset. It retains at most 32
catalogs with an 8 MiB conservative accounting budget; in-flight snapshots can
also retain their own catalogs. No shared cache contains page-buffer references.

Decoders bound lengths/counts before allocation, enforce nesting and schema-chain
limits, and validate markers, page ownership, IDs, fingerprints, fields, and
bitmap padding. Normal compact failures throw `LiteException.CORRUPT_DOCUMENT`
with collection, schema ID, and document address. Rebuild reports/skips damaged
compact documents under the existing recovery policy; it never guesses another
schema. Public BSON and unrelated engine metadata retain their existing codecs.

## Validation and measurements

Run `python3 scripts/test-compact-compatibility.py` for generated v8, mixed-v10,
array-only-v10, and encrypted fixtures, old-engine rejection in direct/shared
modes, and a downgrade read by LiteDB 5.0.21. Run the existing vector compatibility
script too. Focused tests are selected with `FullyQualifiedName~Compact`.

The benchmark harness is `tools/CompactStorage`. See the immutable
[measurement artifacts](https://github.com/litedb-org/LiteDB-Artifacts/tree/d0451a8d4854ffccc65becbad5135938307edbd1/pull-requests/2937-compact-document-storage)
for each implementation stage, raw runs, methodology, storage savings, and
remaining throughput costs. The
measurements predate the `Auto` policy and compare explicit compact and legacy
writes.
