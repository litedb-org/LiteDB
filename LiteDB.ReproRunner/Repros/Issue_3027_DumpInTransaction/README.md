# PR 3027: `$dump` or `$page_list` inside an explicit transaction breaks its next safepoint

A regression since 5.0.21, fixed by [PR #3027](https://github.com/litedb-org/LiteDB/pull/3027).
A transaction larger than its page limit safepoints its dirty pages into unconfirmed WAL slots, and
its next safepoint or its commit rewrites such a page in place at its previous slot. `$dump` and
`$page_list` read the transaction's own safepointed pages from those slots with a read snapshot that
keeps them pinned until the transaction ends. The in-place rewrite of a pinned frame failed an ENSURE,
which stopped the engine, marked the data file invalid and lost the transaction. 5.0.21 appended every
WAL page, so the same transaction committed.

The repro opens a file database with `Transaction Pages=50`, inserts 1000 rows in an explicit
transaction, reads every page with `$dump(pageID)` (in a second database, `$page_list(pageID)`),
updates the 1000 rows, commits and reopens.

## Expected outcome

- Known bad, LiteDB `6.0.0-prerelease.319` (published from `dev` at `5dd942a7`): exit `0`. A safepoint
  during the page reads (`$dump(41)`, `$page_list(39)`) throws `LiteException` 999 "only idle readable
  pages can be evicted", the engine stops, the data file header is marked invalid, and a reopen finds
  only the row committed before the transaction.
- Candidate (the in-repo source): exit `1`. Every safepoint and the commit succeed, and a reopen finds
  1001 documents, 1000 of them updated, with the header not marked invalid.
- Any other outcome exits `2` and fails the proof.

The permanent guard is `DumpPinnedWalSlot_Tests`; see `.github/safety/regression-proofs.json` and
`docs/rules/safety-evidence.md#regression-proofs`.

```bash
dotnet run --project LiteDB.ReproRunner/LiteDB.ReproRunner.Cli -- run Issue_3027_DumpInTransaction
```
