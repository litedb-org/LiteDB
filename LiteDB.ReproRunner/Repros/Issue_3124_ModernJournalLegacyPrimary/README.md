# Issue 3124: Modern header journal repairs a mismatched legacy primary before refusal

Reproduces [LiteDB issue #3124](https://github.com/litedb-org/LiteDB/issues/3124).

## Expected outcome

Against the published known-bad LiteDB `6.0.0-prerelease.355` pinned in the `.csproj` the repro exits `0` (the bug
reproduces). Against the fixed in-repo source it exits non-zero. The **Regression proof** workflow
requires both; see `docs/rules/safety-evidence.md#regression-proofs`.

The package identifies dev commit `b3c4406231908c6ca29f6d4b2769aa08351d6fc2` in its
NuGet repository metadata. Both CI workflows restore it directly from NuGet.

```bash
dotnet run --project LiteDB.ReproRunner/LiteDB.ReproRunner.Cli -- run Issue_3124_ModernJournalLegacyPrimary
```
