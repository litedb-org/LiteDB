# Issue 2849: random-key transaction page-budget cost

Run the benchmark against the published package and current source:

```sh
dotnet run --project LiteDB.ReproRunner/LiteDB.ReproRunner.Cli -c Release -- run Issue_2849_SafepointCost --report /tmp/issue-2849.json --report-format json
```

The default workload inserts one million shuffled integer IDs with 2,000-character
payloads in one transaction, then deletes the even IDs using a shuffled `IN` list.
Three samples alternate default and 100,000-page budgets. Each sample reopens the
file and checks every surviving ID and its independently computed payload before
its timings can count. The test compares medians and reports both ratios. A
ratio above 2 for either operation confirms the reported performance defect;
unrelated exceptions exit 20. There is no hard-coded absolute timing target.

`LITEDB_REPRO_ROWS` changes the dataset size. Smaller runs are useful for validating
the harness, but passing them does not establish that the million-row workload is
fixed. The earlier sequential-ID insertion and full-scan deletion did not reproduce
the problem, even at one million rows. They do not exercise the same page revisits.

Earlier literal-IN observations on Linux x64, .NET 8, production builds
(`TestingEnabled=false`):

| Source | Default / large insert median | Default / large delete median | Data checks |
| --- | ---: | ---: | --- |
| Reported commit `90788bace880f477c9fb52ff7c0da98c9700bdfe` | 2.942 | 2.352 | Passed |
| dev `a50661a9d1a25b5713586d0096c6325d76bf5dfe` | 2.962 | 2.384 | Passed |

Both observations used the retained random workload at one million rows. Run
without competing benchmarks, and retain repeated measurements before judging a
performance fix. LiteDB 5.0.21 has no configurable transaction page limit, so its
run is a persistence/workload control, not a default-versus-large-budget comparison.

The `IN` list is now passed as a `BsonArray` parameter. The previous `Query.In`
call embedded all 500,000 values in a compiled expression literal, overflowing the
Windows stack before the first deletion finished on both package and source.
Parameterization preserves the shuffled IDs, one deletion transaction, budgets,
and full data controls while avoiding that unrelated expression compilation cost.
The earlier ratios above describe the literal form; see the issue notes for the
parameterized fixture's new observations.

The parameterized fixture reproduces on dev at **2.911x inserts / 2.495x deletes**
in the isolated Linux x64/net8.0 production run on 2026-09-15. All six complete
inserted-row and reopened-survivor checks pass.

The same parameterized fixture on reported source `90788bac` measures
**2.924x inserts / 2.455x deletes**, with all six data checks passing.

[Windows and Linux CI](https://github.com/litedb-org/LiteDB/actions/runs/34944166719) also confirms the parameterized workload: dev
insert/delete ratios are 3.475/2.634 on Windows 2022, 2.691/2.114 on Ubuntu 22.04,
and 2.683/2.163 on Ubuntu 24.04. All package/source data controls pass.
