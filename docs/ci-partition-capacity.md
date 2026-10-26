# CI partition capacity

`scripts/run-ci-tests.ps1 -PartitionSuite` runs disjoint slices in separate
sessions. `tests.runsettings` retains the intentional 300-second session limit.
A timing regression still needs investigation; splitting sessions does not
establish that database operations have acceptable performance.

## Windows timing evidence

Downloaded TRX artifacts from all twelve modern Windows legs of each reference
run. The first two runs were fully green; the third was not. Only its cleanly
completed engine/shared-process sessions inform capacity; the failed x86
.NET 10 engine session (222.4 s, one AllocationProcess failure) is excluded.

| Run | Slowest clean x86 engine wall span | Slowest clean x86 shared-process wall span |
| --- | ---: | ---: |
| [40299652691](https://github.com/litedb-org/LiteDB/actions/runs/40299652691), #3129 | 251.1 s | 239.9 s |
| [40305753724](https://github.com/litedb-org/LiteDB/actions/runs/40305753724), #3061 | 255.1 s | 243.7 s |
| [40277572225](https://github.com/litedb-org/LiteDB/actions/runs/40277572225), #3059 | 182.3 s | 236.1 s |

The new filters regroup each TRX result using its TestDefinitions class/method
name and VSTest's case-insensitive FullyQualifiedName matching. Sum the recorded
result durations for each new slice, excluding the two repeated TestHost guards.
These sums estimate work, not future session wall times: host startup, shutdown,
and runner variability still need margin and fresh Windows validation.

| Slice | Maximum estimated test-duration sum across reference x86 legs |
| --- | ---: |
| engine-shared | 120.8 s |
| engine-allocation | 45.7 s |
| engine (complement) | 93.6 s |
| shared-mapped-process | 81.4 s |
| shared-upgrade-process | 82.7 s |
| shared-process (complement) | 116.5 s |

On the slowest green x86 engine leg (windows-2022 / .NET 9 in 40305753724),
the engine slices estimate 114.2 / 45.7 / 93.6 seconds, respectively. Every
engine slice stays below half the session budget in the reference x86 estimates.
The old shared-process maximum exceeds 70% of the budget, so it is split too.
The largest other x86 partition in these artifacts is mvcc at 161.1 seconds;
all other existing partitions are below the 70% threshold.

## Coverage and accounting

The existing rebuild, compact, and index engine slices retain their membership.
`engine-shared` selects Shared names within the old engine complement, including
CrossProcess_Shared, SharedReaderModel, and shared coordination families.
`engine-allocation` selects Allocation classes, including AllocationProcess.
`engine` retains the rest, including TimeoutAfterWalRestore and Coordinator
families. SharedMappedProcess and SharedCoordinationUpgradeProcess each get a
session; other process tests remain in shared-process.

Every suite first checks unfiltered discovery and requires:
`Partition coverage: N test methods, 0 not in exactly one partition`.
The final remaining complement continues to include new namespaces automatically.
Each session still executes both runtime/architecture and engine-hook guards.
The guard methods intentionally repeat. Subtract `2 * (partition count - 1)`
from total result counts and compare case counts per method with the original
suite.

`evidence-leg.json` is generated from the same groups table. The Safety evidence
checker enumerates that manifest rather than hard-coding partition names, so new
slices get the existing nonempty, clean-completion, discovery, and guard checks.
The workflow matrix and its ci-evidence.json expectations are unchanged. No tests,
assertions, concurrency, fault injection, skips, or timeout settings change.

For a before/after case-count comparison, the original Linux TRX artifacts in
40299652691 tested merge commit `68396de72448b4e7e7cd62f149d2377ed312cefe`.
Its Git tree (`8e76ad7a9e1badc3eb3d3528d959e912a6e16285`) exactly equals
`dev` commit `74ecf5c0c09ce1591fc4f203503e791f88580de1`, the base of this
partition change. Those original 12-partition suites report 5,246 rows, or
5,224 cases after removing repeated guards. Unfiltered discovery alone reports
only 5,097 rows because some theories expand their data only at execution; use
the original TRX case counts per method when comparing complete suite totals.
