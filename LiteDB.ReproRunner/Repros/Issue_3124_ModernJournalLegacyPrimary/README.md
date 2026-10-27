# Issue 3124: Modern header journal repairs a mismatched legacy primary before refusal

Reproduces [LiteDB issue #3124](https://github.com/litedb-org/LiteDB/issues/3124).

## Expected outcome

Against the known-bad LiteDB `0.0.0-knownbad.b3c440623190` pinned in the `.csproj` the repro exits `0` (the bug
reproduces). Against the fixed in-repo source it exits non-zero. The **Regression proof** workflow
requires both; see `docs/rules/safety-evidence.md#regression-proofs`.

```bash
dotnet run --project LiteDB.ReproRunner/LiteDB.ReproRunner.Cli -- run Issue_3124_ModernJournalLegacyPrimary
```
