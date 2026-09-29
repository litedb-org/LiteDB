# PR 3027: commits acknowledged behind a torn WAL frame are lost at recovery

A defect fixed by [PR #3027](https://github.com/litedb-org/LiteDB/pull/3027). WAL frames carry a
CRC, and recovery stops at the first frame whose checksum fails, discarding everything behind it. A
WAL append that fails part-way leaves a torn frame. When the failure was not an `IOException` (an
exception from a caller's log stream, or `UnauthorizedAccessException` for EACCES), the engine only
rolled the transaction back and kept committing. If the truncation of the torn frame failed too, or
a buffering caller log stream (a `BufferedStream`) held the frame and tore it only when it wrote it
on later, the next commits were acknowledged behind the torn frame and lost at the next recovery.
Fixed: the failure is recorded, the engine continues read-only (decision 6 of
`docs/decisions/durability-policy.md`), and it refuses every later write before changing a byte.

The repro runs a database on caller streams with `TransactionPageLimit = 1`, so an insert writes
its pages to the WAL at safepoints before its commit. It commits 20 rows, then an insert of 30 rows
fails while its WAL frame is torn, and two more inserts follow. The repro recovers the bytes the
streams hold, which is what a killed process leaves. It runs two scenarios:

- The log stream stores half of the insert's third frame, throws, and then fails the truncation.
- A 64 KiB `BufferedStream` over a log stream that tears the first frame it receives.

## Expected outcome

- Known bad, LiteDB `6.0.0-prerelease.319` (published from `dev` at `5dd942a7`): exit `0`. In both
  scenarios the inserts of `_id` 200 and 201 are acknowledged, and recovery returns only rows 1-20.
- Candidate (the in-repo source): exit `1`. Both later inserts and an update are refused with
  "Cannot modify this database: an earlier write failed". The engine reads rows 1-20 and writes
  nothing more. Recovery returns rows 1-20, and the recovered database takes and keeps new commits.
- Any other outcome exits `2` and fails the proof.

The permanent guards are `TornWalAppend_Tests` and `TornSlotRewrite_Tests`; see
`.github/safety/regression-proofs.json` and `docs/rules/safety-evidence.md#regression-proofs`.

```bash
dotnet run --project LiteDB.ReproRunner/LiteDB.ReproRunner.Cli -- run Issue_3027_TornWalAppend
```
