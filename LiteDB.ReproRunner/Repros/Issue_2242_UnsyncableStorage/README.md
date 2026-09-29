# Issue 2242: a database on storage that cannot sync can no longer be created or checkpointed

Reproduces the regression of [LiteDB issue #2242](https://github.com/litedb-org/LiteDB/issues/2242)
since 5.0.21, found in [PR #3027](https://github.com/litedb-org/LiteDB/pull/3027) and fixed by slice S09 of [#3051](https://github.com/litedb-org/LiteDB/issues/3051) (branch `split/09-durability-protocol`). Some storage
answers every `fsync` with `EINVAL`, `EROFS` or `ENOTSUP` (for example FUSE or virtual file systems
without sync support). 5.0.21 synced with `FileStream.Flush(true)`, which on Unix ignores exactly
these errors. The native device sync that replaced it reports them, and only the WAL degraded: every
data-file sync threw, so such a database could not be created, converted or checkpointed.

The repro installs a seccomp filter that answers `fsync` and `fdatasync` of its own process with
`EINVAL`, checks that a real file can no longer be synced, then creates, indexes, writes,
checkpoints and reopens a database with `Durable Commits=false` (the opt-out for such storage).
Linux x64 and arm64 only.

## Expected outcome

- Known bad, LiteDB `6.0.0-prerelease.319` (published from `dev` at `5dd942a7`): exit `0`. Creating
  the database throws `FileSyncException` "Device sync of '...' failed (errno 22)".
- Candidate (the in-repo source of the S09 branch): exit `1`. Every step succeeds and the reopened database holds every
  row and its index.
- Any other outcome exits `2` and fails the proof.

The permanent guard is `UnsyncableDataFile_Tests`; see `.github/safety/regression-proofs.json` and
`docs/rules/safety-evidence.md#regression-proofs`.

```bash
dotnet run --project LiteDB.ReproRunner/LiteDB.ReproRunner.Cli -- run Issue_2242_UnsyncableStorage
```
