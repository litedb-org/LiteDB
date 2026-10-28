# Issue 3142: Deleting a saturated asymmetric HNSW center throws Queue empty

Reproduces [LiteDB PR #3142](https://github.com/litedb-org/LiteDB/pull/3142).

## Expected outcome

Against the known-bad LiteDB `0.0.0-knownbad.6b2ecd3af268` pinned in the `.csproj` the repro exits `0` (the bug
reproduces). Against the fixed in-repo source it exits `2` and prints `FIXED_VERIFIED`. The **Regression proof** workflow
requires both; see `docs/rules/safety-evidence.md#regression-proofs`.

For a local run, first pack the pinned commit into a local feed; it is not a
published NuGet package. Run from the repository root (the CI proof workflow
prepares this feed automatically):

```bash
python3 .github/scripts/regression_proof.py pack-known-bad \
  --commit 6b2ecd3af26829c52d964da9dc0c33d67c7753a4 --feed /tmp/litedb-knownbad-feed
export RestoreAdditionalProjectSources=/tmp/litedb-knownbad-feed
dotnet run --project LiteDB.ReproRunner/LiteDB.ReproRunner.Cli -- run Issue_3142_AsymmetricDelete
```
