# Issue 3141: Asymmetric inbound HNSW edges point to freed slots after endpoint deletion

Reproduces [LiteDB issue #3141](https://github.com/litedb-org/LiteDB/issues/3141).

## Expected outcome

Against the known-bad LiteDB `6.0.0-prerelease.319` pinned in the `.csproj` the repro exits `0` (the bug
reproduces). Against the fixed in-repo source it exits `2` and prints `FIXED_VERIFIED`. The **Regression proof** workflow
requires both; see `docs/rules/safety-evidence.md#regression-proofs`.

```bash
dotnet run --project LiteDB.ReproRunner/LiteDB.ReproRunner.Cli -- run Issue_3141_IncomingOnlyDelete
```

The unchanged historical fixture is checked before mutation. Both package and source
runs verify document preservation and checkpoint/reopen; a harness error exits `1`.
