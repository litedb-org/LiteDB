# Legacy file validation and recovery

Opening a legacy database distinguishes `LiteException.COLLATION_MISMATCH`
(141) from `INVALID_DATAFILE_STATE` (999). A runtime fingerprint mismatch remains
an ordering compatibility error. A descending pair or duplicate unique key whose
comparison is unchanged from the released comparer is corruption. Known signed
ObjectId and rounded mixed-number ordering changes remain eligible for index
migration. Culture-sensitive strings and container comparisons can be ambiguous
without the original runtime; an ordering difference alone does not authorize
salvage of those values.

Early v5 packages stored `LIMIT_SIZE` at header offset 92. Those bytes are not a
collation fingerprint, including after an interrupted checksum or format
promotion. The committed index-order revision publishes the fingerprint. The
rebuild reader also interprets the later zero limit field as unlimited, matching
ordinary engine opens.

Malformed BSON discovered during migration preflight is reported as corruption
with its collection and data-block address. Device/read failures retain their
original exception; they do not authorize salvaging a partially read record.

## Explicit salvage when a normal open fails

Use a writable file connection with `AutoRebuild=true`:

```csharp
using var db = new LiteDatabase(new ConnectionString
{
    Filename = "damaged.db",
    Connection = ConnectionType.Direct,
    AutoRebuild = true
});
var errors = db.GetCollection("_rebuild_errors").FindAll().ToArray();
```

This is the recovery entry point when construction fails before an instance
`Rebuild()` can be called. It uses the existing replacement/recovery-marker
protocol, retains the original backup, and records the opening diagnostic along
with salvage errors in `_rebuild_errors`. Preserve the backup and review that
report: a successful salvage does not mean every original record was readable.
Shared-mode replacement admission still applies. Replacement validation cannot
recursively trigger another opening rebuild.

Opening index validation does not mark or checkpoint a rejected source.
`ReadOnly=true` prevents automatic salvage, even when `AutoRebuild=true` or the
invalid-state flag is already set. Healthy v5 files awaiting index migration can
be inspected with `ReadOnly=true; LegacyIndexScan=true`; the scan fallback avoids
seeking with a different comparer. A normal writable open migrates indexes and
allows new writes. V4 files require the explicit `Upgrade=true` boundary.

## Automated evidence

`Issue3022LegacyDamage_Tests` and `Issue3022WalRecovery_Tests` cover ordering diagnostics, source byte preservation,
read-only refusal to repair, first-open salvage, preserved unrelated data and
data/WAL backup pairs, recorded loss, persisted new writes, denied shared admission, interrupted
promotion headers, and repeated installation failures at seven rebuild phases.
`Issue2812*` retains genuine collation rejection and recovery coverage; `Issue2417`
retains plain/encrypted loop salvage with the opening error now recorded.
The broader index migration and rebuild suites cover durable publication,
interrupted recovery, and replacement rollback. The fault model uses injected
I/O/install failures and the existing simulated power-loss streams; it is not a
claim about arbitrary storage devices ignoring durable flushes.

`scripts/test-release-compatibility.py` consumes the immutable artifacts revision
in `tools/ReleaseCompatibility/artifacts-revision.txt`, verifying archive and
database SHA-256 values before opening disposable copies. CI runs it on Linux/ICU
and Windows/NLS for every PR and full run. The manifest must contain exactly one
plain and one encrypted database for all 22 stable v5 and six stable v4 packages.

Each database contains 1,024 documents across integer, Guid, ObjectId and string
primary-key collections. The corpus includes integer extrema, long values,
double/decimal values, booleans, nulls, UTC timestamps, Unicode, binary blobs up to
20 KB, nested and empty documents/arrays, secondary unique indexes, arrays and
computed indexes (v5). Tests compare every payload, sample primary/secondary seeks
against scans, verify sorted traversal and array membership, append a document to
each existing collection, roll back an extra insert, and reopen twice (including
read-only). V5 read-only inspection preserves bytes; v4 read-only rejection also
preserves bytes before the separate writable upgrade.

The original #1603 attachment linked by #3022 is downloaded from its original
public URL with a pinned hash, never republished in the synthetic corpus. Baseline
`11e9ffacc` reports code 0 for writable/read-only opens with and without AutoRebuild.
The fixed engine salvages 6,824 directory documents, records six diagnostics and
preserves a newly inserted record after reopen.

Generate a new corpus on Linux with Docker using
`python3 scripts/generate-release-corpus.py <artifacts-repo>/compatibility/released`.
The generator uses original NuGet packages in a pinned .NET Core 3.1 runtime image,
verifies scans and every payload using the writer, and records package/assembly
hashes. Early v5 writers do not produce healthy indexes on .NET 8, so the historical
writer runtime is part of the fixture provenance. The current reader uses .NET 8.
