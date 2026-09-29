# PR 3027: a data file that lost its header beside its WAL opens as a new, empty database

A defect fixed by [PR #3027](https://github.com/litedb-org/LiteDB/pull/3027). Commits go to the
WAL and reach the data file only at a checkpoint. A power loss can keep the WAL but lose the data
file's header. This happens on storage that cannot sync with `Durable Commits=false`
([#2242](https://github.com/litedb-org/LiteDB/issues/2242)), or on a device that acknowledged a sync
it never did. A new data file is then left empty, or its header page was never written back. Beside
such a WAL, the engine initialized a new, empty database over an empty data file, and recovery
discarded every WAL frame, so every committed row was lost without an error. Fixed (decision 11 of
`docs/decisions/durability-policy.md`): every WAL generation starts with a header frame, a copy of
the data header, and the open restores a lost data header from it.

The repro writes 50 rows and an index with `CHECKPOINT = 0` and `Durable Commits=false`, then
closes. The commits stay in the WAL, and the data file holds only its header page. A copy of the
undamaged pair holds every row. The repro then damages the data file alone, as a power loss leaves
it, and opens the database again. It never touches the WAL. The damage is one of these:

- the data file is emptied;
- its header page is zeroed;
- its first sector is zeroed.

## Expected outcome

- Known bad, LiteDB `6.0.0-prerelease.319` (published from `dev` at `5dd942a7`): exit `0`. The open
  of the empty data file creates a new database with no collection (0 of 50 rows), and the WAL is
  emptied. The known-bad version refuses a zeroed header page with "File is not a valid LiteDB
  database format"; the repro stops at the empty data file.
- Candidate (the in-repo source): exit `1`. For each of the three kinds of damage, the open restores
  the header from the WAL with all 50 rows and the index. The database then takes a new commit and
  keeps it when reopened.
- Any other outcome exits `2` and fails the proof.

The permanent guards are `HeaderFrame_Tests` and `HeaderFrameCrash_Tests`; see
`.github/safety/regression-proofs.json` and `docs/rules/safety-evidence.md#regression-proofs`.

```bash
dotnet run --project LiteDB.ReproRunner/LiteDB.ReproRunner.Cli -- run Issue_3027_LostHeaderBesideWal
```
