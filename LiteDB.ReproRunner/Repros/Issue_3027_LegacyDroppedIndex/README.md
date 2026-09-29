# PR 3027: a collection whose index 5.0.21 dropped cannot be written

A regression since 5.0.21, fixed by [PR #3027](https://github.com/litedb-org/LiteDB/pull/3027).
5.0.21 never cleared the bytes after a collection page's index list, so `DropIndex` leaves the tail
of the dropped entries there. Since vector indexes (#2678) the engine read a vector section after the
index list without checking that the page lists a vector index. It took the stale bytes for vector
metadata, and every insert into the collection failed, after the writable open had already
converted the file.

The repro copies `customers.db` of `LiteDB.Tests/Resources/DropIndex_5_0_21.zip` (written by the
5.0.21 package: indexes Name, Age and CustomerId, 200 documents, then `DropIndex("Age")`), then
inserts, updates, deletes, re-creates the index and reopens.

## Expected outcome

- Known bad, LiteDB `6.0.0-prerelease.319` (published from `dev` at `5dd942a7`): exit `0`. The first
  insert throws `LiteException` 999 "request page must be less or equals lastest page in data file".
- Candidate (the in-repo source): exit `1`. Every write succeeds and the reopened file holds 200
  documents with consistent indexes.
- Any other outcome exits `2` and fails the proof.

The permanent guard is `LegacyDroppedIndex_Tests` and the 5.0.21 compatibility script; see
`.github/safety/regression-proofs.json` and `docs/rules/safety-evidence.md#regression-proofs`.

```bash
dotnet run --project LiteDB.ReproRunner/LiteDB.ReproRunner.Cli -- run Issue_3027_LegacyDroppedIndex
```
