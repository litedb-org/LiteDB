# Cumulative query performance through optimization 19

Ordinary LINQ queries in this comparison take **26–44% less time** after
recompilation, without changing the query API or using explicit bindings.
Counts, existence checks, and small sorted pages also improve substantially.
Queries that previously missed selective index access show much larger gains.

This compares the original shared-IR implementation (`57a94728`) with all
nineteen subsequent changes (`3a4d822a`), using the same fresh harness on both
assemblies. It does not include the original text-to-IR translator improvement
and must not be combined arithmetically with older benchmark percentages.

## Complete-query results

| Complete query | Baseline µs | Steps 1–19 µs | Time reduction | Baseline B/query | Steps 1–19 B/query |
|---|---:|---:|---:|---:|---:|
| Ordinary LINQ primary-key lookup | 41.47 | 25.77 | 37.9% | 36,920 | 28,123 |
| Ordinary LINQ combined predicate | 64.19 | 36.29 | 43.5% | 38,820 | 27,362 |
| Ordinary LINQ indexed projection | 100.79 | 74.74 | 25.8% | 52,003 | 40,020 |
| SQL primary-key lookup | 37.96 | 27.59 | 27.3% | 39,055 | 31,484 |
| Equality OR, LINQ | 30,910.07 | 40.85 | 757× faster | 40,879,544 | 44,464 |
| Bounded range, LINQ | 14,725.47 | 53.79 | 274× faster | 19,485,176 | 44,720 |
| Contradictory range, LINQ | 14,822.96 | 16.22 | 914× faster | 19,465,384 | 9,320 |
| Enabled optional guard, LINQ | 30,088.99 | 27.78 | 1,083× faster | 40,879,270 | 25,472 |
| Contains plus range, LINQ | 970,418.21 | 368.82 | 2,631× faster | 42,251,280 | 352,112 |
| Shared leading OR guard, LINQ | 34,047.63 | 126.34 | 269× faster | 43,145,120 | 94,891 |
| Secondary-index range count | 14,162.39 | 2,146.21 | 84.8% | 17,586,128 | 5,192,584 |
| Full primary-index count | 10,520.95 | 2,951.02 | 72.0% | 18,945,760 | 8,353,328 |
| Indexed Exists, LINQ | 62.08 | 20.55 | 66.9% | 39,875 | 22,928 |
| First ten sorted rows | 62,169.17 | 28,234.98 | 54.6% | 39,639,848 | 35,148,696 |
| Mixed-order sorted page | 92,903.93 | 41,382.64 | 55.5% | 46,060,897 | 41,528,896 |
| Full-scan control | 60,492.47 | 56,376.60 | 6.8% | 51,532,700 | 48,794,016 |

The plan-changing examples are deliberately selective. Equality OR returns two
rows, the bounded range returns ten, and Contains/range intersects a thousand
candidate keys with a narrow range. Their large ratios describe avoided work in
these cases, not a universal database speedup. The scan control still reads every
row and improves only modestly. The separate measurements in the incremental
report identify which changes contribute to each workload.

## Reproduction and limits

- Production Release assemblies, `TestingEnabled=false`, .NET 8.0.30 on Ubuntu
  24.04 / AMD Ryzen 9 3900X. CPU affinity is fixed to CPU 2 and tiered compilation
  is disabled. No concurrent local builds or tests run during measurement.
- Warm in-memory database: 20,000 documents, indexes on `_id`, `Score`, and `City`.
  Each timed operation constructs, executes, and consumes a complete query.
  Ordinary LINQ lookups/projections change captured parameters between calls.
- Fresh before/after/after/before processes, each with a full warmup batch and
  nine timed batches. Tables report medians of eighteen batches per version.
  All consumed-result checksums match. Database setup and warmup are excluded.
- A shared host introduces timing variation. These measurements do not establish
  cold-start performance, disk throughput, or behavior at different selectivity.
  Retain allocation and individual-batch results when interpreting small changes.

Build each revision and the same harness as described in the
[incremental report](query-optimization-benchmarks.md), then use the `overall`
workload selector:

```sh
DOTNET_TieredCompilation=0 taskset -c 2 dotnet /tmp/opt-bench/QueryOptimizationBenchmarks.dll label overall
python3 tools/QueryIrBenchmarks/compare.py --before docs/benchmarks/query-optimization/overall-19-before-1.json docs/benchmarks/query-optimization/overall-19-before-2.json --after docs/benchmarks/query-optimization/overall-19-after-1.json docs/benchmarks/query-optimization/overall-19-after-2.json
```

[Raw measurements and plans](benchmarks/query-optimization) use the
`overall-19-before-*` / `overall-19-after-*` filenames. The `overall` selector
also avoids later benchmark helpers that inspect cache internals unavailable in
the baseline library. The underlying query operations are identical on both.
