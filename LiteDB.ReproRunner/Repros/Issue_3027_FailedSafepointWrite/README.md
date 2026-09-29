# PR 3027: Commit publishes a transaction whose safepoint write failed

A defect fixed by [PR #3027](https://github.com/litedb-org/LiteDB/pull/3027). A transaction that
holds more pages than `TransactionPageLimit` writes its dirty pages to the WAL at a safepoint before
it commits. Any operation can reach a safepoint, including a query. When that write failed with an
exception the engine does not treat as fatal (any non-I/O exception, such as
`UnauthorizedAccessException` for EACCES/EPERM or an exception from a caller's log stream), only the
query reported it. The explicit transaction stayed active although its failed pages had been
discarded. `Commit` then published the rest, and every read of the database failed afterwards, also
after reopening. Fixed: such a transaction can only roll back. A later read, write or `Commit`
throws "can only be rolled back" (a write or `Commit` also rolls it back), and the data stays intact.

The repro runs a database on caller streams with `TransactionPageLimit = 6`. It commits and
checkpoints row 1 of an indexed collection and 100 large documents. An explicit transaction then
inserts rows 2 to 4. A query over the large documents pushes the transaction past its page limit,
and the caller log stream fails the safepoint's frame write before writing anything. The repro then
reads row 1 and calls `Commit`. A second run tries an insert and then calls `Commit`.

## Expected outcome

- Known bad, LiteDB `6.0.0-prerelease.319` (published from `dev` at `5dd942a7`): exit `0`.
  `Commit` returns `true`. Every read then throws `LiteException` "get only index below highest
  index", also after the streams are reopened.
- Candidate (the in-repo source): exit `1`. The read, the insert and `Commit` throw "Writing this
  transaction's pages failed, so it can only be rolled back". The database holds row 1, its index
  and the 100 documents. It takes a new commit and keeps it when reopened.
- Any other outcome exits `2` and fails the proof.

The permanent guard is `FailedSafepointWrite_Tests`; see `.github/safety/regression-proofs.json` and
`docs/rules/safety-evidence.md#regression-proofs`.

```bash
dotnet run --project LiteDB.ReproRunner/LiteDB.ReproRunner.Cli -- run Issue_3027_FailedSafepointWrite
```
