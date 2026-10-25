# PR 3059: acknowledged writes disappear with non-writable data and no caller log

[Review finding](https://github.com/litedb-org/LiteDB/pull/3059#discussion_r4372117216).
The defect also exists on dev `3e0ae587f171c68f9cc954d91a4951e0f89fd258`.
A real file is seeded with a committed indexed row, opened as a non-writable caller
stream through `LiteEngine`, and given no log. An explicit insert/commit succeeds,
but reopening the file loses the inserted row because the only WAL was engine-owned.
The fix refuses the insert before acknowledgement while allowing the read transaction
to commit. The original file bytes, row, index result and caller ownership are checked.

- Package `6.0.0-prerelease.319`: exit 0, `REPRODUCED` (acknowledged row lost).
- Candidate source: exit 1, `FIXED` (write refused and original state preserved).
- Any other error/state: exit 2, `INCONCLUSIVE`, which fails the proof.

The permanent `ReadOnlyStorageNoLog_Tests` guard covers both APIs, memory/file streams,
automatic/explicit transactions, other write operations, read commit/rollback, and
retention through a caller-owned file log with repeated reopen and disposal rollback.

```bash
dotnet run --project LiteDB.ReproRunner/LiteDB.ReproRunner.Cli -- run Issue_3059_ReadOnlyStorageNoLog
```
