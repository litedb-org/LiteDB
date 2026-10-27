# Issue 3137: Durable promotion journal recovers a primary whose identity was torn away

Reproduces [LiteDB issue #3137](https://github.com/litedb-org/LiteDB/issues/3137).

## Expected outcome

Against the known-bad LiteDB `0.0.0-knownbad.5d3cc5c248b9` pinned in the `.csproj` the repro exits `0` (the bug
reproduces). Against the fixed in-repo source it exits non-zero. The **Regression proof** workflow
requires both; see `docs/rules/safety-evidence.md#regression-proofs`.

```bash
dotnet run --project LiteDB.ReproRunner/LiteDB.ReproRunner.Cli -- run Issue_3137_TornPromotionJournal
```
