# Compact document storage (experimental opt-in)

Issue: https://github.com/litedb-org/LiteDB/issues/2920

Enable `CompactStorage = true` in `EngineSettings` or `ConnectionString`, or use
`Filename=data.db;Compact Storage=true`. The default remains legacy BSON writes.
Both modes read BSON and compact documents. Enabling the option does not scan,
rewrite, or rebuild existing documents. Read-only and idle opens do not promote
the file. Public `BsonSerializer` continues to produce BSON.

The first beneficial compact write durably promotes the file to v10 before schema
or document pages can reach the WAL. Ordinary files remain v8, vectors require
at least v9, and promotion is monotonic through rollback, replay, and checkpoint.
A rolled-back first compact write can leave a v10 file containing only BSON.
Older engines reject v10; simultaneous writable access by different engine
versions is not supported.

`Rebuild(new RebuildOptions { CompactStorage = true })` converts useful shapes
and regenerates catalogs through the existing temporary-file/backup process.
`CompactStorage = false` explicitly writes legacy BSON to the rebuilt file;
vector data/indexes still impose the v9 floor. A null rebuild option retains the
engine's setting. The setting on the existing engine remains unchanged after
rebuild; reopen without compact writes before using a downgraded file with old
software. Explicit rebuild options retain the existing password/collation
semantics: supply them when needed.

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

The reproducible benchmark is `tools/CompactStorage`. See
[measurement results](benchmarks/2920/README.md) for each implementation stage,
raw runs, methodology, storage savings, and remaining throughput costs. The
format remains opt-in; these measurements do not decide a default rollout.
