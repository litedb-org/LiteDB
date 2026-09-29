# PR 3027: the 5.0.21 log of another database is replayed into a 5.0.21 data file

A defect fixed by [PR #3027](https://github.com/litedb-org/LiteDB/pull/3027). Legacy (5.x) WAL pages
carry no checksum and nothing that ties them to their data file. The open replayed every committed
page of the log beside a 5.0.21 data file into it: the log of another database overwrote the header,
wrote that database's pages into the file and was then deleted, so the file's own collections were
gone. The fix refuses such an open with `INVALID_DATABASE` and changes neither file. The same guards
cover committed legacy pages that are not pages or lie beyond both files, and checksummed frames
beside a legacy header.

The repro places `foreign-log.db` of `LiteDB.Tests/Resources/ForeignWal_5_0_21.zip` (the WAL of
another database, written by the 5.0.21 package) beside `crash.db` of `WalCrash_5_0_21.zip` and
opens it.

Of those variants this one has bounded damage on the known bad. The foreign log names pages up to
1732, so the data file grows to at most 14 MB. The repro checks the fixtures' SHA-256 and caps every
file it writes with `RLIMIT_FSIZE` at 64 MiB, with SIGXFSZ ignored. A page far beyond both files then
fails with EFBIG (exit `2`) and cannot grow a sparse file to terabytes, as the converted-WAL variant
does on the known bad. Linux only.

## Expected outcome

- Known bad, LiteDB `6.0.0-prerelease.319` (published from `dev` at `5dd942a7`): exit `0`. The open
  succeeds. The data file grows from 40960 to 14196736 bytes, the log is deleted, and the database
  now holds the other database's collections `big` and `fresh` instead of its 100 `docs`.
- Candidate (the in-repo source): exit `1`. The open fails with `INVALID_DATABASE` ("commits the
  header of another database") and changes neither file. Beside its own 5.0.21 WAL the same data file
  then opens with all 101 documents, 21 of them with value 7.
- Any other outcome exits `2` and fails the proof.

The permanent guard is `LegacyWalPageBound_Tests` (it also reads `ForeignWal_5_0_21.zip`),
`ConvertedWalBesideLegacyHeader_Tests` and the 5.0.21 compatibility script. See
`.github/safety/regression-proofs.json` and `docs/rules/safety-evidence.md#regression-proofs`.

```bash
dotnet run --project LiteDB.ReproRunner/LiteDB.ReproRunner.Cli -- run Issue_3027_ForeignLegacyWal
```
