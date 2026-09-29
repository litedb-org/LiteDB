# PR 3027: a checkpoint on a log that cannot sync overwrites the data file behind a recovery copy only in the OS cache

A defect found in [PR #3027](https://github.com/litedb-org/LiteDB/pull/3027) and fixed by slice S09 of [#3051](https://github.com/litedb-org/LiteDB/issues/3051) (branch `split/09-durability-protocol`). A checkpoint overwrites
the data file in place behind a header journal and the WAL, its recovery copy. With
`durable commits=false` on storage whose log cannot sync ([#2242](https://github.com/litedb-org/LiteDB/issues/2242):
the sync request is refused, for example with `EINVAL`), the known-bad engine went ahead with that
recovery copy only in the OS cache. A power loss mid-overwrite tore the data file, with nothing on the
device to repair it from, so rows the data file held before the checkpoint were lost. Fixed (decision D
and implementation note 12 of `docs/decisions/durability-policy.md`): the checkpoint refuses before it
writes the journal. It writes nothing and keeps the WAL, and a checkpoint drains the WAL once the log
syncs. Opting out gives up recent commits, never the data file's integrity.

The repro runs the database on two caller streams (`EngineSettings.DataStream`/`LogStream`), each a
`FileStream` subclass that models a device (`DeviceFile.cs`). Both engine versions call its
`Flush(true)` for a device sync. Each file keeps a device image that only a successful sync updates;
the writes since then are pending, in order. The data file always syncs; once armed, the log answers every sync with `EINVAL`,
which both versions treat as "cannot sync".

1. Rows 1-10 and a value index are committed, checkpointed and synced (both files sync).
2. The log stops syncing. An engine with `durable commits=false` commits rows 11-30 to the WAL and runs
   a checkpoint.
3. If the checkpoint wrote to the data file, the repro takes the device images at its data sync. A power
   loss before that sync leaves the data file with a prefix of the pending writes, the last one whole or
   torn in half. The log is left as of its last successful sync. Each image is opened as a copy.

## Expected outcome

- Known bad, LiteDB `6.0.0-prerelease.319` (published from `dev` at `5dd942a7`): exit `0`. The log
  refuses the checkpoint's journal syncs, and the checkpoint writes 11 pages anyway, 6 of them over
  pages the device held. 19 of the 22 power-loss images do not hold rows 1-10 intact. The first is
  `PageChecksumException: Checksum mismatch in Data file at position 16384`; others fail with
  "get only index below highest index" or `EndOfStreamException`.
- Candidate (the in-repo source of the S09 branch): exit `1`. The checkpoint returns 0 and writes nothing to the data
  file. The WAL stays byte for byte, and the engine reads rows 1-30 with no failure recorded. A power
  loss keeps rows 1-10 intact and loses only the unsynced WAL's rows 11-30; a process crash keeps them
  all. Once the log syncs, a checkpoint drains the WAL, and a power loss after it keeps rows 1-30.
- Any other outcome exits `2` and fails the proof.

The permanent guards are `OverwriteBarrier_Tests` and
`Issue2242_UnsyncableLog_Tests.Compact_promotion_on_a_log_that_never_syncs_falls_back_to_bson_without_durable_commits`;
see `.github/safety/regression-proofs.json` and `docs/rules/safety-evidence.md#regression-proofs`.

```bash
dotnet run --project LiteDB.ReproRunner/LiteDB.ReproRunner.Cli -- run Issue_3027_OverwriteBehindUnsyncedLog
```
