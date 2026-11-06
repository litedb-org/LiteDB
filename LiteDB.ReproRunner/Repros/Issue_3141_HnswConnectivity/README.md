# Issue 3141: HNSW saturated neighborhoods disconnect stored vectors

Reproduces [LiteDB issue #3141](https://github.com/litedb-org/LiteDB/issues/3141).

## Expected outcome

Against the known-bad LiteDB `6.0.0-prerelease.319` pinned in the `.csproj` the repro exits `0` (the bug
reproduces). Against the fixed in-repo source it exits `2` (`FIXED_VERIFIED`); configuration or harness errors exit `1`. The **Regression proof** workflow
requires both; see `docs/rules/safety-evidence.md#regression-proofs`.

```bash
dotnet run --project LiteDB.ReproRunner/LiteDB.ReproRunner.Cli -- run Issue_3141_HnswConnectivity
```

The program inserts 40 identical nonzero vectors before creating the index. The
old nearest-eight pruning saturates a neighborhood and isolates later nodes:
bounded ANN returns fewer than 40 even though ef=160, while an exact primary scan
and document count both return 40. It checks the index plan and repeats the ANN
observation after file-backed checkpoint/reopen. Equal-score order is irrelevant.

The same scenario also reproduces against packed dev
`b3c4406231908c6ca29f6d4b2769aa08351d6fc2`. The permanent tests additionally replay
the original 20-node sampled levels, check all-metric mutation recall, force bridge
subdivision and articulation repair, and exercise rollback/torn-WAL recovery.
