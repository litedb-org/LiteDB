# PR 3027: converting a 5.0.21 file while a shared reader holds a snapshot discards its WAL

A regression since 5.0.21, fixed by [PR #3027](https://github.com/litedb-org/LiteDB/pull/3027).
The first writable open converts a 5.0.21 file. It checkpointed the legacy WAL and then cleared it,
assuming the checkpoint had drained it. The checkpoint is lease-aware: while a shared reader holds a
snapshot it backfills only up to that snapshot. The conversion truncated the legacy WAL anyway, so
every transaction 5.0.21 had committed there was lost, and the converted file no longer opens in
5.0.21 either.

The repro copies `WalCrash_5_0_21.zip` (a crash image written by the 5.0.21 package: 101 documents,
21 of the commits only in the WAL), holds a live reader lease as another process's shared reader
does, and opens the file with `Connection=shared`. The archive lives in
[LiteDB-Artifacts](https://github.com/litedb-org/LiteDB-Artifacts) at the revision and SHA-256
pinned in `LiteDB.Tests/Resources/artifacts.json` (provenance: `LiteDB.Tests/Resources/RegressionFixtures.md`).
The repro resolves it with the tests' `ArtifactFixtures`; set `LITEDB_ARTIFACTS_DIR` to a local copy
to run offline.

## Expected outcome

- Known bad, LiteDB `6.0.0-prerelease.319` (published from `dev` at `5dd942a7`): exit `0`. The
  conversion runs, the legacy WAL shrinks from 516096 to 32768 bytes, and 100 documents with 0 of the
  21 WAL commits remain.
- Candidate (the in-repo source): exit `1`. The open is refused with `LOCK_TIMEOUT` and changes
  neither file; once the reader is gone the conversion keeps all 101 documents.
- Any other outcome exits `2` and fails the proof.

The permanent guard is `LegacyWalSharedMigration_Tests` and the 5.0.21 compatibility script; see
`.github/safety/regression-proofs.json` and `docs/rules/safety-evidence.md#regression-proofs`.

```bash
dotnet run --project LiteDB.ReproRunner/LiteDB.ReproRunner.Cli -- run Issue_3027_LegacyWalConversion
```
