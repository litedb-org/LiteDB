# PR 3027: a 5.0.21 file on a read-only stream cannot be opened

A regression since 5.0.21, fixed by [PR #3027](https://github.com/litedb-org/LiteDB/pull/3027).
`new LiteDatabase(stream)` over a stream that cannot be written (a `FileStream` opened with
`FileAccess.Read`, an embedded resource, a read-only share) opened a writable engine, and every
writable open of a 5.0.21 file migrates it first, which writes: the open failed with
`NotSupportedException`. The constructor has no ReadOnly or Legacy Index Scan switch, so the file could
not be read at all. 5.0.21 read the same stream without writing.

The repro copies `customers.db` of `LiteDB.Tests/Resources/DropIndex_5_0_21.zip` (written by the 5.0.21
package: 200 documents, indexes Name and CustomerId, Age dropped) and opens it through a `FileStream`
with `FileAccess.Read`, then through a read-only `MemoryStream` beside an empty writable log stream.

## Expected outcome

- Known bad, LiteDB `6.0.0-prerelease.319` (published from `dev` at `5dd942a7`): exit `0`. Both opens
  throw `NotSupportedException` "Stream does not support writing."; the second one first writes 32768
  bytes into the writable log stream.
- Candidate (the in-repo source): exit `1`. Both open read-only, read all 200 documents and their
  indexed queries, reject an insert, and change neither the file nor the log stream; a writable open of
  the file afterwards migrates it with every document.
- Any other outcome exits `2` and fails the proof.

The permanent guard is `LegacyReadOnlyStream_Tests`; see `.github/safety/regression-proofs.json` and
`docs/rules/safety-evidence.md#regression-proofs`.

```bash
dotnet run --project LiteDB.ReproRunner/LiteDB.ReproRunner.Cli -- run Issue_3027_LegacyReadOnlyStream
```
