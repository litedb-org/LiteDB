# PR 3027: a format promotion that waited on a failed checkpoint writes and syncs the file again

A defect found in [PR #3027](https://github.com/litedb-org/LiteDB/pull/3027) and fixed by slice S09 of [#3051](https://github.com/litedb-org/LiteDB/issues/3051) (branch `split/09-durability-protocol`). With
`CompactStorage = Compact`, the first compact write into a v11 file promotes it to v12. Under the
header lock, the promotion writes the new header into the data file and syncs both files. A
checkpoint holds the same header lock (its commit lock) and the WAL writer while it syncs the data
file. When that sync failed, the checkpoint stopped the engine only after releasing its locks, and
the promotion checked no engine state. A compact insert that had passed its per-document check and
waited for the header lock promoted the file after the failure. It wrote a v12 header and synced the
data file again on the handle whose sync had just failed (fsyncgate: the retry reports success).
Fixed: under the WAL writer, the promotion throws the failure that stopped the engine, or the
recorded one, before it syncs or writes anything (decision 6 of
`docs/decisions/durability-policy.md`).

The repro creates a v11 database on caller streams with `CompactStorage = Legacy` and
`CHECKPOINT = 0`. Rows 1-20 reach the data file and row 21 stays in the WAL as a single commit, so
the checkpoint retires no frame. The repro reopens the streams with `CompactStorage = Compact` and
runs one insert on its own thread. The insert stores a first document as BSON (the first of its
shape) and a second document of the same shape compactly, which promotes the file. The second
document is a `BsonDocument` whose indexer holds the thread when the engine reads its `_id` for
compact encoding. That read comes after the per-document engine check and before the promotion.
Then a checkpoint runs on another thread. Its first data sync (the caller data stream's `Flush`)
releases the insert, waits until the insert blocks on the header lock the checkpoint holds, and
fails with EIO. The insert can reach the promotion only after the checkpoint releases the header
lock, which it holds until the sync has failed, so no timing decides the outcome. The device model
journals every stream read, write, truncation and sync. After the failure, it answers syncs with
success, as fsyncgate storage does.

## Expected outcome

- Known bad, LiteDB `6.0.0-prerelease.319` (published from `dev` at `5dd942a7`): exit `0`. After
  the failed data sync, the insert syncs the log and writes a v12 header over the v11 one. It then
  syncs the data file again, and that sync reports success. Finally it truncates and syncs the log.
  It writes no header journal of its own, because the checkpoint's journal is still outstanding.
  What the insert does after the promotion races with the engine's teardown, and the repro does not
  check it.
- Candidate (the in-repo source of the S09 branch): exit `1`. The insert is refused with the checkpoint's failure
  ("Engine closed after an I/O failure"). Neither file is written or synced after the failed sync,
  and the data header stays v11. A copy of the streams' bytes reopens with rows 1-21 and only the
  compact collection's seed. The copy then takes compact inserts, which promote it to v12, and keeps
  them.
- Any other outcome exits `2` and fails the proof.

The permanent guard is `CheckpointFailureWindow_Tests`; see
`.github/safety/regression-proofs.json` and `docs/rules/safety-evidence.md#regression-proofs`.

```bash
dotnet run --project LiteDB.ReproRunner/LiteDB.ReproRunner.Cli -- run Issue_3027_PromotionAfterFailedCheckpoint
```
