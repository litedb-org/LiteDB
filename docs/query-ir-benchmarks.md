# Shared query IR benchmarks

Measured on 2026-09-16 using an AMD Ryzen 9 3900X, Ubuntu 24.04 x64,
.NET 8.0.30, SDK 10.0.400, Release builds with `TestingEnabled=false`.
Both processes were pinned to CPU 2 with tiered compilation disabled. No build
or test process ran concurrently with the recorded benchmark pairs.

Baseline: `828d760fb686501a43dcf8163d9519cf1bf1521d`. Implementation: the shared
query IR and binding changes in this PR. The later upstream base commit
`8654cbe8` changes only CI and a connection-string test, so the measured baseline
library is also the library at that base.

Three separate process pairs ran in alternating order: before/after,
after/before, before/after. Each process collected 15 samples per workload after
warmup. The tables report the median of all 45 samples. All shared workloads'
consumed-result checksums matched across revisions and processes.

The in-memory database contains 10,000 deterministic documents and `_id`, `Age`,
and `City` indexes. Query benchmarks include expression construction, planning,
execution, and result consumption/materialization. They do not include database
creation or index building. Raw samples, allocation counts, Gen0 measurements,
and checksums are in [benchmarks/query-ir](benchmarks/query-ir).

## Results

| Workload | Before µs | After µs | Time reduction | Before B/op | After B/op | Allocation reduction |
|---|---:|---:|---:|---:|---:|---:|
| translate-point | 8.29 | 3.92 | 52.7% | 4592 | 2184 | 52.4% |
| translate-nested | 30.68 | 12.06 | 60.7% | 12692 | 5794 | 54.3% |
| translate-contains | 13.01 | 5.20 | 60.1% | 6576 | 2632 | 60.0% |
| translate-methods | 43.59 | 19.03 | 56.3% | 20330 | 9492 | 53.3% |
| translate-projection | 9.99 | 8.38 | 16.1% | 7576 | 4480 | 40.9% |
| construct-query | 41.09 | 20.25 | 50.7% | 16348 | 8635 | 47.2% |
| e2e-id | 50.76 | 42.98 | 15.3% | 38462 | 35854 | 6.8% |
| e2e-index-equality | 74.48 | 66.31 | 11.0% | 45228 | 42612 | 5.8% |
| e2e-index-range | 97.90 | 91.94 | 6.1% | 61874 | 58642 | 5.2% |
| e2e-index-projection | 103.04 | 96.61 | 6.2% | 52258 | 47610 | 8.9% |
| e2e-repeated-parameters | 85.09 | 62.99 | 26.0% | 42677 | 35738 | 16.3% |
| e2e-full-scan | 27662.36 | 27203.82 | 1.7% | 28888376 | 28883984 | 0.0% |
| e2e-sql-id | 38.44 | 37.37 | 2.8% | 38245 | 37845 | 1.0% |

| Reusable binding workload | µs/op | B/op | Gen0 / 1,000 ops |
|---|---:|---:|---:|
| bind-point | 0.66 | 992 | 0.00 |
| e2e-bound-id | 30.55 | 33893 | 4.00 |
| e2e-bound-repeated-parameters | 35.19 | 31000 | 3.00 |

Ordinary end-to-end indexed queries took **6–26% less time** in these runs.
The changing-parameter query improved from 85.09 to 62.99 µs; the point lookup
improved from 50.76 to 42.98 µs. Reusing a bound template reduced those costs
further to 35.19 and 30.55 µs respectively. Binding still allocates independent
parameter/predicate objects; it avoids repeated translation and compilation.

Translation took 16–61% less time and allocated 41–60% fewer bytes across the
five measured shapes. The projection case shows the smallest translation gain.
The full scan and SQL control were effectively unchanged; differences of a few
percent on this shared host are not evidence of a reliable speedup.

For context on variation, the three per-process point-lookup medians were
49.91/50.76/50.79 µs before and 44.06/42.46/42.22 µs after. Indexed projection
medians were 102.43/105.26/102.92 µs before and 96.47/95.81/100.76 µs after.
These are local measurements, not a guarantee for disk-bound workloads or other
hardware. Full scans are dominated by document execution/materialization, while
small indexed queries expose more frontend cost.

## Reproduction

See [the harness instructions](../tools/QueryIrBenchmarks/README.md). The runner
can compile against either revision's production assembly without copying the
old translator into production or introducing a compatibility switch. Reproduce
the table from the checked-in data with:

```sh
python3 tools/QueryIrBenchmarks/compare.py \
  --before docs/benchmarks/query-ir/before-*.json \
  --after docs/benchmarks/query-ir/after-*.json
```
