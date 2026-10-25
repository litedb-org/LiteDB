# Legacy file validation and recovery

## Released-file corpus

`scripts/test-release-compatibility.py` consumes the immutable artifacts revision
in `tools/ReleaseCompatibility/artifacts-revision.txt`, verifying archive and
database SHA-256 values before opening disposable copies. It also checks that
`tools/ReleaseCompatibility/Corpus.cs` and the generator match the hashes recorded
in the corpus manifest, so the expected documents are the ones the writers used.
CI runs it for every PR on Linux/ICU and Windows/NLS; full runs add Windows/ICU and
macOS/ICU. The manifest must contain exactly one plain and one encrypted database
for all 22 stable v5 and six stable v4 packages.

Each database contains 1,024 documents across integer, Guid, ObjectId and string
primary-key collections. The corpus includes integer extrema, long values,
double/decimal values, booleans, nulls, UTC timestamps, Unicode, binary blobs up to
20 KB, nested and empty documents/arrays, secondary unique indexes, arrays and
computed indexes (v5). Tests compare every payload, sample primary/secondary seeks
against scans, verify sorted traversal and array membership, append a document to
each existing collection, roll back an extra insert, and reopen twice (including
read-only). V5 read-only inspection preserves bytes; v4 read-only rejection also
preserves bytes before the separate writable upgrade.
This is a clean-file corpus, not exhaustive compatibility coverage for every
historical WAL, interrupted operation or stale index layout. Those states need
their own focused migration and recovery regressions.

`tools/ReleaseCompatibility/pending-activation.json` lists fixtures whose checks
fail until a named engine fix lands, with the owner and reason for each. They stay
in the inventory and hash checks. The script still runs them and requires the
recorded diagnostic and unchanged source bytes; when one passes, the script fails
until its entry is removed. Currently the v5.0.0 to v5.0.5 files are pending: those
writers stored `LIMIT_SIZE` in header bytes 92..95, which the current engine reads
as a collation stamp.

Generate a new corpus on Linux with Docker using
`python3 scripts/generate-release-corpus.py <artifacts-repo>/compatibility/released`.
The generator uses original NuGet packages in a pinned .NET Core 3.1 runtime image,
verifies scans and every payload using the writer, and records package/assembly
hashes. Early v5 writers do not produce healthy indexes on .NET 8, so the historical
writer runtime is part of the fixture provenance. The current reader uses .NET 8.
Commit the output to LiteDB-Artifacts, then pin the new revision in
`artifacts-revision.txt`; the database files never enter this repository.
