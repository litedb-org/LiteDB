# Memory-management benchmark results

This is the original PR revision's impact report. The later shared-reader and
profile results are in [memory-profile-results.md](memory-profile-results.md).

Measured 2026-09-12, comparing the `dev` merge base (`61fa785a`) with the
implementation head (`e75c3fda`). These measurements are intended to quantify
the memory/latency trade-off of the new default rather than serve as portable
absolute performance claims.

## Test environment and method

- AMD Ryzen 9 3900X (12 cores / 24 threads), 64 GiB RAM, NVMe storage,
  Linux 6.8.
- Release `net8.0`, Microsoft.NETCore.App 8.0.30. Both variants were compiled
  from the exact commits above and run in separate processes.
- One database created by the merge-base build was copied for both variants:
  120,000 documents, a 900-character payload and an eight-float vector per
  document; 169,803,776 bytes (161.94 MiB) on disk.
- Except for the cache-size sensitivity table, the merge base used its existing
  unbounded cache and the implementation used its 64 MiB file-cache default.
- Each reported latency is the median of three independent processes. Point
  workloads ran nine batches per process, discarded the first two, and used the
  median remaining batch. Full-scan rows are medians of the corresponding scan
  number across three fresh processes. Large insert and index rows are medians
  of three fresh databases/copies.
- Memory was sampled with the database still open after two forced full GCs.
  `cache allocated` comes from `$database`; managed heap and process working set
  were sampled after the same collection. The host file cache was left warm, so
  scan timings compare LiteDB cache behavior rather than physical cold-storage
  latency.
- Lower is better for every table except throughput. Small timing differences
  should be treated as noise. The `cache allocated` values follow exactly from
  the reported number of allocated 8 KiB frames; managed-heap and working-set
  values are observed process-level measurements without an allocation
  breakdown.

## Retained memory

| Workload after completion | Measurement | `dev` | PR | Change |
|---|---:|---:|---:|---:|
| Third full scan, 120k documents | cache allocated | 169.23 MiB | 64.06 MiB | **-62.15%** |
| Third full scan, 120k documents | forced-GC managed heap | 172.20 MiB | 65.45 MiB | **-61.99%** |
| Third full scan, 120k documents | working set | 230.49 MiB | 128.58 MiB | **-44.21%** |
| Plain bulk insert, 120k documents | forced-GC managed heap | 172.39 MiB | 66.41 MiB | **-61.48%** |
| Encrypted bulk insert, 120k documents | forced-GC managed heap | 172.45 MiB | 66.47 MiB | **-61.46%** |
| Integer index build, 120k documents | forced-GC managed heap | 171.47 MiB | 65.49 MiB | **-61.81%** |
| Vector index + searches, 5k documents | forced-GC managed heap | 13.62 MiB | 10.62 MiB | **-22.00%** |
| 50k distinct compiled expressions | retained managed growth | 142.05 MiB | 0.34 MiB | **-99.76%** |

The scan, insert and integer-index cases all reach the same underlying result:
the previous cache retains all 21,662 allocated frames, while the PR stops at
8,200 frames (64.06 MiB after segment rounding). The expression case is
independent of the page cache and demonstrates that retained delegates stop
growing once the combined 1,000-entry limit is reached.

## Latency

| Workload | `dev` | PR | PR change |
|---|---:|---:|---:|
| First cache-fill scan, 120k documents | 1.558 s | 1.674 s | +7.5% |
| Third repeated scan, 120k documents | 345.8 ms | 468.9 ms | +35.6% |
| Hot single-reader point lookup | 18.366 us/op | 19.136 us/op | +4.2% |
| Plain bulk insert, 120k documents | 2.226 s | 2.486 s | +11.7% |
| Encrypted bulk insert, 120k documents | 2.319 s | 2.583 s | +11.4% |
| Integer index build, 120k documents | 1.621 s | 1.974 s | +21.8% |
| Vector index build, 5k documents | 8.117 s | 8.515 s | +4.9% |
| Vector top-20 search, 5k documents | 0.526 ms | 0.539 ms | +2.5% |
| Compile/execute 50k distinct expressions | 10.520 s | 10.293 s | -2.2% (neutral) |

The repeated-scan result is the expected cost of no longer retaining a database
larger than the configured cache. The 2.2% expression timing difference is
within measurement noise; the meaningful result is the removal of essentially
all retained growth from that workload.

## Shared-reader scaling

Each worker performs point lookups against the same `LiteDatabase` and a warmed
4,000-document hot set. The table reports median batch wall time divided by the
total operations; lower per-operation time and higher relative throughput are
better.

| Readers | `dev` | PR | Latency change | PR throughput change |
|---:|---:|---:|---:|---:|
| 1 | 19.993 us/op | 19.931 us/op | -0.3% | +0.3% |
| 4 | 9.209 us/op | 11.704 us/op | +27.1% | -21.3% |
| 8 | 7.828 us/op | 12.556 us/op | +60.4% | -37.7% |
| 16 | 12.243 us/op | 19.166 us/op | +56.6% | -36.1% |

Single-reader behavior is unchanged, while latency rises materially at four or
more concurrent readers. This is consistent with contention in the new
single-lock cache, but lock profiling is still required to attribute the cause.

## Cache-size sensitivity

This reruns the three-scan workload at the PR's supported cache sizes. The
allocated value includes segment rounding. `dev` has no limit setting.

| Configuration | Cache allocated after scan | Managed heap | Working set | Third scan | vs `dev` latency |
|---|---:|---:|---:|---:|---:|
| `dev` default (unbounded) | 169.23 MiB | 172.20 MiB | 230.49 MiB | 345.8 ms | baseline |
| PR, 8 MiB target | 8.06 MiB | 8.56 MiB | 67.87 MiB | 463.2 ms | +34.0% |
| PR, 64 MiB target (default) | 64.06 MiB | 65.45 MiB | 128.58 MiB | 468.9 ms | +35.6% |
| PR, 256 MiB target | 162.06 MiB | 165.46 MiB | 229.05 MiB | 350.0 ms | +1.2% |

When the target can hold this 161.94 MiB database, repeated-scan performance is
within 1.2% of the old unbounded cache. The measured cache allocation is 918
frames (7.17 MiB) lower than `dev`; the benchmark does not separately attribute
that difference to segment slack or another cause. For sequential scans with
little reuse, the 8 and 64 MiB settings have similar throughput, while 8 MiB
retains another 55.9 MiB less managed memory.

## Conclusions and open performance work

- The default converts file-size-proportional cache retention into a stable
  64.06 MiB allocation in these large-file workloads: about 62% less retained
  managed/cache memory for a 161.94 MiB database. The percentage increases for
  larger databases because the PR remains bounded while `dev` continues to
  grow.
- The compiled-expression leak is removed independently of database size:
  retained growth falls from 142.05 MiB to 0.34 MiB for 50,000 unique sources.
- The bounded default necessarily gives up the old engine's full-database cache
  advantage. Users whose working set fits in memory can select a larger
  `CacheSize`; 256 MiB restores this scan case to practical parity.
- The shared-reader regression needs profiling before claiming neutral
  highly-concurrent read performance. If lock profiling confirms the cache lock
  as the bottleneck, candidate work is reducing its critical section or
  sharding lookup/pin bookkeeping while retaining atomic frame publication and
  eviction.
- Follow-up benchmark coverage should add per-lookup p50/p99 distributions,
  .NET 10, ARM64, slow storage, and longer mixed read/write runs. These are
  performance-characterization gaps, not evidence of unbounded retention.
