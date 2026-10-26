# CI partition capacity

`scripts/run-ci-tests.ps1 -PartitionSuite` runs disjoint slices in separate
sessions. `tests.runsettings` retains the intentional 300-second session limit.
A timing regression still needs investigation; splitting sessions does not
establish that database operations have acceptable performance.

## Windows timing evidence

Downloaded TRX artifacts from all twelve modern Windows legs of each reference
run. The first two and fourth runs were fully green; the third was not. Only
its cleanly completed engine/shared-process sessions inform capacity; the failed x86
.NET 10 engine session (222.4 s, one AllocationProcess failure) is excluded.

| Run | Slowest clean x86 engine wall span | Slowest clean x86 shared-process wall span |
| --- | ---: | ---: |
| [40299652691](https://github.com/litedb-org/LiteDB/actions/runs/40299652691), #3129 | 251.1 s | 239.9 s |
| [40305753724](https://github.com/litedb-org/LiteDB/actions/runs/40305753724), #3061 | 255.1 s | 243.7 s |
| [40277572225](https://github.com/litedb-org/LiteDB/actions/runs/40277572225), #3059 | 182.3 s | 236.1 s |
| [40352465353](https://github.com/litedb-org/LiteDB/actions/runs/40352465353), refreshed #3061 | 249.7 s | 239.2 s |

## .NET Framework capacity

The Framework legs use the native xunit.console runner and currently execute the
whole assembly in one session. Run [40379238513](https://github.com/litedb-org/LiteDB/actions/runs/40379238513)
completed `net462` in 1,048 seconds (17:28) but its `net481` leg was cancelled
at the 20-minute job limit after 1,215 seconds (20:15), before it could report a
result. The earlier complete #3127 run [40258582707](https://github.com/litedb-org/LiteDB/actions/runs/40258582707)
took 1,079 seconds (17:59) for `net481` and 690 seconds (11:30) for `net462`.

Partitioning this separate xunit.console path would require replacing the
single XML evidence partition and adding equivalent discovery/result accounting;
the full assembly is retained so no Framework tests are filtered out. Its job
timeout is therefore raised to **35 minutes** (2,100 seconds). Even the current
1,215-second cancellation point leaves 885 seconds, or 42.1% headroom, and the
previous completed `net481` run leaves 1,021 seconds, or 48.6%. This is a timeout
capacity guard, not a claim that the Framework suite is fast; future growth or
another cancellation should trigger a dedicated xunit partitioning change.

The new filters regroup each TRX result using its TestDefinitions class/method
name and VSTest's case-insensitive FullyQualifiedName matching. Sum the recorded
result durations for each new slice, excluding the two repeated TestHost guards.
These sums estimate work, not future session wall times: host startup, shutdown,
and runner variability still need margin and fresh Windows validation.

| Slice | Maximum estimated test-duration sum across available x86 evidence |
| --- | ---: |
| engine-shared | 120.8 s |
| engine-allocation-wal | 97.5 s |
| engine-allocation-stress | 145.9 s |
| engine-allocation (complement) | 32.2 s |
| engine (complement) | 93.6 s |
| shared-mapped-process | 81.4 s |
| shared-upgrade-process | 82.7 s |
| shared-process (complement) | 116.5 s |

On the slowest green x86 engine leg (windows-2022 / .NET 9 in 40305753724),
the pre-split engine slices estimate 114.2 / 45.7 / 93.6 seconds, respectively.
The allocation values in the table are superseded by the transient-stall split
below; every resulting allocation session remains below the 180-second target.
The old shared-process maximum exceeds 70% of the budget, so it is split too.
In the original three runs, the largest other x86 partition was mvcc at
161.1 seconds. The refreshed run has one engine-index session at 212.7 seconds
(70.9% of the budget), on windows-latest x86 / .NET 10. Two existing
IndexMigrationPowerLoss cases account for 186.3 seconds there; the other five
x86 engine-index sessions take 22.3–37.5 seconds. This outlier crosses the 70%
capacity-review threshold and needs fresh Windows observation. Its existing
partition membership and the 300-second limit are retained.

Run 40352465353 tests the exact tree of `dev` at `6cd2cd362`, including #3058
and #3061. Its slowest x86 engine leg estimates 100.4 / 62.9 / 84.7 seconds
for the three pre-split engine slices. Across the four reference runs, all six
original split-slice estimates stay below 121 seconds. Newly added
regression tests leave the x86 remaining partition at a maximum 94.3 seconds of
test-duration sum in the refreshed run (95.9 seconds measured wall span).

## Measured allocation capacity and split (round 2)

Run [40367004916](https://github.com/litedb-org/LiteDB/actions/runs/40367004916)
tested head `7f46f15835bdd6fe4dcf204b29319dcdd29d4e45` through merge candidate
`f8b6017e3a51bbb5566e9e13200fa9a2abb9ddaa`. The new allocation session on
[windows-latest x86 / .NET 8](https://github.com/litedb-org/LiteDB/actions/runs/40367004916/job/121729629845)
passed 104/104 results but took **277.291 seconds**, leaving only **7.57%**
headroom. This fails the requested minimum **40% headroom** (at most 180 seconds
per session). This was a **transient single-leg stall** during one contiguous
interval; it is not evidence that every allocation run has a persistent
277-second cost. The earlier regrouped estimates did not protect the session
against this outlier, so the allocation classes are now split into disjoint
sessions.

Ranked individual results from artifact `test-results-test-windows-3` against the
retained original `test-results-test-windows-0` TRX from
[x64 / .NET 8 attempt 1](https://github.com/litedb-org/LiteDB/actions/runs/40367004916/job/121729629610):

| Class | Cases | x86 duration sum (s) | x64 duration sum (s) |
| --- | ---: | ---: | ---: |
| AllocationWalFailure | 26 | 97.469 | 3.204 |
| AllocationConcurrent | 4 | 53.839 | 0.771 |
| AllocationPowerLoss | 6 | 47.277 | 4.651 |
| AllocationOracle | 4 | 44.769 | 0.418 |
| AllocationPublication | 38 | 22.068 | 8.108 |
| AllocationProcess | 12 | 8.098 | 5.276 |
| AllocationFailure + AllocationPublicationFailure | 12 | 1.986 | 1.409 |

The two guards contribute another 0.007 / 0.003 seconds. Total test-duration
sums are 275.512 / 23.839 seconds; session wall spans are 277.291 / 25.441
seconds. The x64 job failed an unrelated engine-shared case, but this allocation
session completed cleanly. Its replacement artifact from attempt 2 instead
records 63.605 seconds wall time and 61.829 seconds of test durations; these
attempts must not be confused. All other five x86 allocation sessions in this
run take 26.063, 27.475, 32.950, 33.081 and 25.646 seconds.

The new selectors assign the retained x86 timings as follows:

| New session | Classes | x86 duration sum (s) | Headroom to 180 s |
| --- | --- | ---: | ---: |
| `engine-allocation-wal` | `AllocationWalFailure_Tests` | 97.469 | 82.531 s |
| `engine-allocation-stress` | `AllocationConcurrent_Tests`, `AllocationPowerLoss_Tests`, `AllocationOracle_Tests` | 145.885 | 34.115 s |
| `engine-allocation` (complement) | `AllocationPublication_Tests`, `AllocationProcess_Tests`, `AllocationPublicationFailure_Tests`, `AllocationFailure_Tests`, and future allocation classes | 32.152 | 147.848 s |

These sums are disjoint and total the original 275.512 seconds of test work,
while each session is below the documented 180-second target even under the
observed stall timings. Session wall time still includes host and runner
overhead, so a fresh Windows x86 run remains the required CI validation.

The largest individual x86 cases are encrypted power-loss/tail (25.677 seconds),
plain and encrypted failed-safepoint rollback (24.125 / 18.042 seconds), and
plain power-loss/hole (16.466 seconds). All four oracle cases take 9.162–13.930
seconds, versus 0.094–0.117 seconds on the original x64 leg. All four concurrency
cases take 13.054–13.800 seconds, versus 0.165–0.225 seconds. Across the **17
cases exceeding three seconds**, the excess over x64 is **229.947 seconds**:
17 × 13.526 seconds on average. They account for **91.37%** of the total
251.673-second per-test excess. These 17 cases do not start child processes.

The timing sequence matters: 19 torn-WAL cases first complete below two seconds
each; the last five take 6.901–11.443 seconds. The next two safepoint, four
oracle, four concurrency and first two power-loss cases are slow. The remaining
four power-loss cases return to 0.479–2.436 seconds. The contiguous slow interval
is approximately 17:26:41–17:30:35 UTC. This establishes a transient slowdown
across several non-process test paths, but does **not** identify the blocked
operation or justify attributing it to runner variance, disk latency, GC or x86.

The two proposed process-helper explanations do not account for the excess:

- `AllocationProcess` takes 8.098 seconds for all 12 cases, only 2.822 seconds
  above x64. Each case, including child start, barrier, kill, release wait and
  repeated recovery, finishes in 0.567–1.122 seconds on the affected x86 leg.
- `WindowsFileRelease.WaitForFiles` does not identify handle owners. Its caller,
  `MvccProcess.ReleaseFiles`, excludes shared files when a parent/peer remains
  open. AllocationProcess kills its child before the parent opens recovery.
  `WaitForFiles` throws the first sharing violation at its deadline; it does not
  silently continue after five seconds. A permanent parent-held lock would fail
  the test. The slow non-process cases never call this helper. Their TempFile
  disposal uses `Retry`, which also throws at its deadline for a persistent lock.

The retained x86 worker-diagnostics artifact is an intentional dump smoke test
at 17:42:53 UTC, after this interval, and supplies no stack evidence for it.
Root cause remains unresolved. Capture timed file open/write/flush calls,
sharing retries, runtime pauses and thread waits on the affected Windows leg,
keeping the original system-temp volume and existing assertions. A green rerun
or an estimated redistribution alone would not establish a root-cause fix.
The partition split addresses the documented capacity contract without changing
the 300-second policy; the fresh Windows measurement will determine whether the
transient stall recurs.

## Coverage and accounting

The existing rebuild, compact, and index engine slices retain their membership.
`engine-shared` selects Shared names within the old engine complement, including
CrossProcess_Shared, SharedReaderModel, and shared coordination families.
`engine-allocation-wal` and `engine-allocation-stress` select the named heavy
classes; `engine-allocation` is their complement and includes AllocationProcess
plus future Allocation classes automatically.
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
assertions, concurrency, fault injection or skips change; modern sessions retain
their 300-second limit, while the Framework job limit is raised as documented
above.

For a before/after case-count comparison, the original Linux TRX artifacts in
40299652691 tested merge commit `68396de72448b4e7e7cd62f149d2377ed312cefe`.
Its Git tree (`8e76ad7a9e1badc3eb3d3528d959e912a6e16285`) exactly equals
`dev` commit `74ecf5c0c09ce1591fc4f203503e791f88580de1`, the base of this
partition change. Those original 12-partition suites report 5,246 rows, or
5,224 cases after removing repeated guards. Unfiltered discovery alone reports
only 5,097 rows because some theories expand their data only at execution; use
the original TRX case counts per method when comparing complete suite totals.

The refreshed baseline is the original 12-partition Linux net8/net10 suites in
run 40352465353. Their tested merge commit `44366286ddb909fd92170b6ea4df1a7ad614a5f5`
has tree `e8cccd2df6ee104181ef12a5f391a8f5ce9382f9`, identical to `dev` at
`6cd2cd362fdd63ca8bd6225fde4b44b48fc11ad0`. Each baseline has 5,406 passed,
three existing skips, and 5,409 rows: 5,387 cases after subtracting 22 repeated
guards. The 18-partition equivalent has 34 repeated guards, so its matching
total is 5,421 rows. This baseline includes the new WAL safety coverage.
`AllocationWalFailure_Tests` belongs to engine-allocation-wal; the concurrent,
power-loss and oracle classes belong to engine-allocation-stress; all five LegacyWal
classes, WalWriteFailureWindow, and the other new regression classes belong to
remaining. Discovery checks every method, including these classes, against all
18 selectors before execution.

Fresh Linux net8/net10 suites on the refreshed source should complete all 18
partitions with 5,418 passed, the same three existing skips, and 5,421 rows.
After removing 34 repeated guards, both match all 5,387 baseline cases, including
per-method counts, test-case names and outcomes. Both discovery verifiers report
2,449 entries (2,447 distinct method names), with zero gaps or overlaps. Removing
or duplicating allocation fails with 11 coverage problems on each target, and
missing-result controls reject each of the six additional partition names.
The largest measured local sessions are engine-shared at 185.3 seconds on net8
and 185.6 seconds on net10; these Linux spans do not predict Windows wall times.
