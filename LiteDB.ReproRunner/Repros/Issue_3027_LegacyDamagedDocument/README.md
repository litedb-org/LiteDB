# PR 3027: a 5.0.21 file with one damaged document cannot be opened, even with Auto-Rebuild

A regression since 5.0.21, fixed by [PR #3027](https://github.com/litedb-org/LiteDB/pull/3027).
5.0.21 opens a file with one damaged document, reads the others, and its rebuild keeps all three. A
writable open of the current format must first migrate every index from the documents, which the
damage prevents. The failed migration surfaced only an internal error, and even with
`Auto-Rebuild=true` the first open failed; only a later open rebuilt the file.

The repro copies `damaged.db` of `DamagedDocument_5_0_21.zip` (LiteDB-Artifacts, pinned and
hash-verified through `LiteDB.Tests/Resources/artifacts.json`; set `LITEDB_ARTIFACTS_DIR` for offline
runs; written by the 5.0.21 package; the BSON length of one string in document 2 overwritten) and
opens it with `Auto-Rebuild=true`.

## Expected outcome

- Known bad, LiteDB `6.0.0-prerelease.319` (published from `dev` at `5dd942a7`): exit `0`. The first
  open throws `LiteException` 999 "string length exceeds the document limit". The second open
  rebuilds the file but drops document 2, whose readable part 5.0.21 keeps.
- Candidate (the in-repo source): exit `1`. The first open rebuilds the file and keeps documents 1
  and 3 and the readable part of document 2; `damaged-backup.db` equals the original file byte for
  byte, and a plain reopen finds the 3 documents. The open recovers explicitly (#3022 opening
  recovery): it neither writes a rebuild mark nor retries the open.
- Any other outcome exits `2` and fails the proof.

The permanent guard is `LegacyDamagedDocument_Tests`; see
`.github/safety/regression-proofs.json` and `docs/rules/safety-evidence.md#regression-proofs`.

```bash
dotnet run --project LiteDB.ReproRunner/LiteDB.ReproRunner.Cli -- run Issue_3027_LegacyDamagedDocument
```
