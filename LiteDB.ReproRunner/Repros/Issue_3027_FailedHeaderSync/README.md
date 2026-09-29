# PR 3027: commits after the recovery of a failed header sync are lost at a power loss

A defect found in [PR #3027](https://github.com/litedb-org/LiteDB/pull/3027) and fixed by slice S09 of [#3051](https://github.com/litedb-org/LiteDB/issues/3051) (branch `split/09-durability-protocol`). When a sync fails
with an I/O error, Linux marks the pages it could not write back clean ("fsyncgate"). They stay in
the page cache without reaching the device, and a later sync of the same file succeeds without
writing them. A checkpoint's WAL salt rotation writes a new data header. When that header's sync
failed, the WAL and its header journal were kept, and the next open read the new header from the
cache. Recovery took that header as published and retired the journal behind a data sync that wrote
nothing. The device kept the header from before the failed sync, so a power loss discarded every
later commit as a stale WAL generation. Fixed (implementation note 13 of `docs/decisions/durability-policy.md`):
recovery makes the journal durable, writes the header back as it read it (the same bytes), and only
then syncs and retires the journal.

The repro runs a database on caller streams (`EngineSettings.DataStream`/`LogStream`) that model
the device. Both versions sync such a `FileStream` subclass through its `Flush(true)`. A write
reaches the file (the page cache) at once, and the device image only at a successful sync. The
repro commits rows 1-10 and runs a checkpoint. The checkpoint's sync of a lone header-page write
(the salt rotation) fails with EIO and forgets that write, as Linux does. The repro then opens the
database again over the same files and commits rows 11-15, acknowledged durable
(`durableLogFlush=true`). A power loss follows: the repro opens the device images.

## Expected outcome

- Known bad, LiteDB `6.0.0-prerelease.319` (published from `dev` at `5dd942a7`): exit `0`. The
  device holds the header from before the failed sync, and the power loss keeps rows 1-10 of the
  acknowledged 1-15.
- Candidate (the in-repo source of the S09 branch): exit `1`. The device holds the header the engine reads, and all 15
  acknowledged rows survive the power loss with their contents. The recovered database takes a new
  commit and keeps it when reopened.
- Any other outcome exits `2` and fails the proof.

The permanent guard is `SyncFailureRecovery_Tests`; see `.github/safety/regression-proofs.json` and
`docs/rules/safety-evidence.md#regression-proofs`.

```bash
dotnet run --project LiteDB.ReproRunner/LiteDB.ReproRunner.Cli -- run Issue_3027_FailedHeaderSync
```
