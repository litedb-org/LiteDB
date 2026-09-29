# PR 3027: `new LiteDatabase(stream)` stores CHECKPOINT=1 and its Dispose throws

A regression since 5.0.21, fixed by [PR #3027](https://github.com/litedb-org/LiteDB/pull/3027).
Without a log stream the WAL of `new LiteDatabase(stream)` lives only in memory, so for a writable
stream that is not a `MemoryStream` the constructor forced every commit into the stream by setting the
CHECKPOINT pragma to 1 (#2652), and `Dispose` restored it through the engine before disposing the
engine. The pragma is stored in the file's header, so a use that ends without `Dispose` leaves
CHECKPOINT=1 for every later open; a second `Dispose` throws; and `Dispose` with an open transaction
throws before the engine is disposed. 5.0.21 did none of this.

The repro uses a `FileStream` three times: a use that only disposes the caller's stream, then a
filename open reads the pragma; a double `Dispose`; and `Dispose` with an open transaction, then a
filename open checks what was kept.

## Expected outcome

- Known bad, LiteDB `6.0.0-prerelease.319` (published from `dev` at `5dd942a7`): exit `0`. The
  filename open finds CHECKPOINT=1; the second `Dispose` throws "This engine instance already
  disposed."; `Dispose` with an open transaction throws "The current thread already contains an open
  transaction."
- Candidate (the in-repo source): exit `1`. CHECKPOINT stays 1000 with both commits kept, the second
  `Dispose` does not throw, and `Dispose` rolls the open transaction back (only `_id` 1 remains).
- Any other outcome exits `2` and fails the proof.

The permanent guard is `StreamDatabaseDispose_Tests`; see `.github/safety/regression-proofs.json` and
`docs/rules/safety-evidence.md#regression-proofs`.

```bash
dotnet run --project LiteDB.ReproRunner/LiteDB.ReproRunner.Cli -- run Issue_3027_StreamDatabaseDispose
```
