# Issue 3142: Deleting a saturated asymmetric HNSW center throws Queue empty

Reproduces [LiteDB PR #3142](https://github.com/litedb-org/LiteDB/pull/3142).

## Expected outcome

Against the known-bad LiteDB `0.0.0-knownbad.6b2ecd3af268` pinned in the `.csproj` the repro exits `0` (the bug
reproduces). Against the fixed in-repo source it exits `2` and prints `FIXED_VERIFIED`. The **Regression proof** workflow
requires both; see `docs/rules/safety-evidence.md#regression-proofs`.

```bash
dotnet run --project LiteDB.ReproRunner/LiteDB.ReproRunner.Cli -- run Issue_3142_AsymmetricDelete
```
