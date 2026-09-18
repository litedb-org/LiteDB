# PR #2905 merge-confidence audit

Audit date: 2026-09-18. Starting PR head: `2a2c74e924ae72a65089d60f406350cd12608180`.
Current upstream `dev` tested: `7d2a16c4313fc24f7904cd558af6528d79128a40`.
The final code under test adds two scanner guards, a conservative residual
contradiction fallback, and fifteen regression cases accompanying this report. The working branch stays on the PR; integration was
tested in a separate, uncommitted merge worktree.

**Assessment: high confidence in the tested correctness and compatibility
contracts; merge remains conditional on fresh CI and acceptance of the documented
performance tradeoffs.** The PR is large: its starting diff has 561 files,
including 97 library files and 72 test files. This is a targeted review with
substantial executable evidence, not an exhaustive proof or a substitute for
maintainer review. See the [PR summary](https://github.com/litedb-org/LiteDB/pull/2905)
for the current CI status and outstanding decisions.

## Questions and evidence

| Question | Answer from this audit |
|---|---|
| Are we testing the actual PR head? | Fetched the GitHub PR ref and verified the full head SHA. The original 48 reported checks were successful; all four review threads were resolved. |
| Does it integrate with current `dev`? | Clean temporary merge with `7d2a16c4`; full .NET 8 suite passes **1,747 tests / 7 skips** after the fixes. This includes overlapping storage-reader changes. |
| Does the complete PR suite pass? | Release solution build passes with `TestingEnabled=true`; **1,619 tests / 7 skips** pass on each of .NET 8 and .NET 10 after the fixes. Reproduction-runner tests: **18 passed**. |
| Can independent checks find wrong query results? | They found two boundary defects, now fixed; a separate targeted check found a contradiction-pruning regression. Three deterministic seeds pass **12,000 parameterized cases / 48,000 assertions** against production net8.0; production netstandard2.0 passes another **2,400 cases / 9,600 assertions** on the .NET 8 runtime. |
| Are regression tests sensitive to the defects? | All **eight boundary theory cases fail against the unfixed integration assembly** and pass after the guards are added. Four contradiction cases fail before/pass after; three additional cases preserve existing behavior. They cover matching and case-mismatched root/nested indexes, collation, duplicate keys, both orders, counts, Exists, and reused SQL/bindings. |
| Are bindings, caches and metadata isolated? | Full suites cover fresh parameters, mapper mutation, cache bounds, concurrent/reentrant use, field ownership, nested evaluators and live index metadata. The production lifetime probe retains **0/64 original arrays**, with 451,160 bytes of measured live-heap growth after 128 complete queries. |
| Are storage and released-file contracts preserved? | Compatibility script passes ordinary v8 round trips and vector-file rejection by LiteDB 5.0.21, both plain and encrypted. Full suites also exercise index maintenance, rollback, checkpoint, small page budgets and reopened files. |
| Can we still produce production artifacts? | Production builds pass for netstandard2.0, net8.0 and net10.0 in separate output directories; NuGet packaging passes. The local worktree package has a detached development version, not a release version. |
| Are public APIs removed? | Reflection comparison of current `dev` and the integration production assembly finds no removed public/protected signatures; `BsonExpression.Bind(BsonDocument)` is the only addition. This is a structural smoke check, not exhaustive binary-compatibility certification. |
| Are performance claims supported at the final implementation? | Fresh current-dev comparisons have matching checksums across 16 complete-query workloads and substantial allocation reductions. Four-CPU runs improve all measured workloads. One-CPU default-tiering runs regress on some controls; retain these results and their runtime sensitivity below. |
| Are size and whitespace requirements met? | C# size and whitespace checks pass; existing recommended-size warnings remain. |
| Is the CI evidence current enough? | The original run was green, but its Linux job checked a merge into `9c7ff75a`, not the newest `dev`. Local integration closes that gap for Linux .NET 8. Fresh cross-platform CI for the audit commit is tracked in the PR summary. |

## Defects discovered and repaired

1. **Reversed BETWEEN bounds could return boundary rows.** `IndexRange` emitted
   keys equal to its start before comparing against its end. For example, an
   indexed `Owner.Score BETWEEN 'I' AND 'A'` returned rows although scalar
   evaluation returned none. Reject empty intervals before emitting any keys,
   using the execution collation. Equal inclusive bounds continue to return all
   duplicates. The new nested/case-insensitive index paths expose this older
   scanner defect to additional predicates.
2. **Sentinel equality could return a skip-list node.** An equality or IN seek
   for `BsonValue.MaxValue` could emit the tail node, producing an invalid data
   address during document lookup or counting a non-document in an aggregate.
   MinValue/MaxValue cannot be stored as index keys; equality now returns no
   document for either sentinel. Mixed IN lists still return their valid keys.

3. **Residual contradiction pruning changed observable errors.** On unindexed
   data, `SUBSTRING(Name,1000) = 'x' AND Score > 7 AND Score < 3` previously
   threw when evaluating the first filter; the PR silently replaced the query
   with empty input. Conversely, `Score > (1 % 0) AND Score < 3` threw during
   planning even on an empty collection. Pruning now requires a safe prefix of
   scalar member-path constraints and falls back on evaluation/comparison errors.
   Contradictions reached before a throwing suffix still use empty input.
   Separate WHERE clauses, empty input, short circuits and reused SQL are tested.
   This was a regression introduced by the PR, unlike the older scanner defects.

The fixes preserve the persisted representation. Independent generation exposed
both boundary defects; a subsequent targeted comparison with current dev exposed
the contradiction regression before its focused tests were written.

## Independent query checker

[`tools/QueryConfidenceChecks`](../tools/QueryConfidenceChecks/Program.cs) generates
nested AND/OR trees with equality, inequality, ranges, IN and BETWEEN. It varies
root/nested paths, missing/null values, mixed numeric representations, strings,
binary, arrays, documents, Boolean values and sentinel parameters. Index field
casing differs from query casing. Each shape executes with four parameter sets,
including resident SQL-cache hits. It checks document IDs, counts and mixed-order
pagination against an in-memory tree evaluator.

The oracle shares BSON comparison but does not share expression parsing,
compilation or query planning. It is not an independent implementation of BSON
collation. Disk, concurrency, INCLUDE and write semantics are covered by the
existing focused suites rather than this checker.

Build the production library with isolated output and supply its DLL explicitly
to avoid overwriting the solution's test-hook assembly:

```bash
dotnet build LiteDB/LiteDB.csproj -c Release -p:TestingEnabled=false \
  --artifacts-path /tmp/litedb-confidence-production
dotnet run --project tools/QueryConfidenceChecks -c Release \
  -p:LiteDBAssembly=/tmp/litedb-confidence-production/bin/LiteDB/release_net8.0/LiteDB.dll \
  -- 2905 150
# Repeat the built checker with seeds 2906 and 2907, each with 300 shapes.
# Also build/run against release_netstandard2.0/LiteDB.dll in separate outputs.
```

Seed summaries and the production lifetime probe are retained in
[`query-confidence-audit`](query-confidence-audit/).

## Fresh performance evidence and limitations

The [complete tables](query-confidence-audit/results.md) and adjacent raw JSON
retain every batch, allocation, checksum and plan. Both sides use production
Release assemblies, .NET 8.0.30, Ubuntu 24.04 and an AMD Ryzen 9 3900X. Before is
current `dev` at `7d2a16c4`; after is its merge with this PR and the scanner fixes.
The `QueryOptimizationBenchmarks` `overall` workload suite consumes queries
over 20,000 documents. Each affinity group runs before/after/after/before; each
process performs warmup and nine measured batches. This is a shared host.

With affinity to CPUs 0–3, the two-process median comparison measures 31–46%
less time for ordinary lookups/projections, 64–76% less for counts/Exists,
54–63% less for small sorted pages, and 16% less for the scan control. Avoided
scans improve much more. Ordinary lookups allocate roughly 35–47% fewer bytes.
These are cumulative measurements against current dev, not multiplied per-step
percentages. Process variability limits precision, especially for short queries.

With affinity to **one CPU** and default tiering, the original suite instead
measures Exists **82% slower**, single-key top-N **43% slower**, and the scan
control **115% slower**. The mixed top-N result is noisy (+4% overall, with large
before-process variation). These unfavorable samples are retained. A diagnostic
one-CPU comparison with `DOTNET_TieredCompilation=0` reverses those regressions:
Exists 62.16 → 20.31 µs, single-key top-N 61.56 → 28.62 ms, mixed top-N
92.42 → 38.26 ms, and scan 57.31 → 50.71 ms. This establishes runtime sensitivity;
it does **not** establish its complete cause or justify discarding default-runtime
results. Disabling tiering is a diagnostic, not a deployment recommendation.

A longer isolated scan with **default tiering on one CPU** uses 60 warmup queries
and nine batches of 60 queries. Its medians are **44.77 → 39.63 ms** (11.5% less
time), with identical checksums and 27% fewer allocated bytes. The first measured
batch is still **69.32 → 116.75 ms** before the later batches converge. This
narrows the concern to warmup-sensitive behavior for this workload; it does not
erase the startup cost or settle every single-CPU workload. Both raw runs are
retained as `warm-before.json` and `warm-after.json`.

To repeat the complete suite, build the benchmark once with `LiteDBAssembly`
pointing at a production DLL, copy its output into separate before/after
directories, and replace only `LiteDB.dll` with the corresponding production
assembly. Run `taskset -c 0-3 dotnet QueryOptimizationBenchmarks.dll LABEL overall`
in before/after/after/before order. Repeat with `taskset -c 2` for one CPU and
with `DOTNET_TieredCompilation=0 taskset -c 4` for the diagnostic pair.
For the longer scan, use `taskset -c 4 dotnet QueryOptimizationBenchmarks.dll LABEL
overall-scan-control 20`. The benchmark dispatcher now accepts an `overall`
prefix so a single workload can be isolated; the measured operations are unchanged.

The paired measurements above were made at audit commit `21481eeea`, before
adding the final contradiction fallback. A subsequent complete 16-workload smoke
run on the final integrated production code retains every baseline checksum;
its raw batches are in `final-after.json`. This last run is not another paired
latency experiment and is not combined with the earlier ABBA timings.

The earlier per-step reports still document planning overhead, cache churn and
the cost of correcting linguistic comparison. This audit does not declare
those tradeoffs repaired. No cold-disk throughput, arbitrary application load,
long-duration soak or universal latency claim follows from these fixtures.

## Decisions and remaining work

- [ ] Confirm fresh cross-platform CI for the audit commit; use the PR summary
  for its live result. Older green checks alone do not close this item.
- [ ] Decide whether the measured runtime/warmup sensitivity and previously
  reported planning/churn regressions are acceptable. Single-CPU or cold-start
  deployments deserve representative workload measurements before promising a
  performance improvement.
- [ ] Obtain maintainer review of the large optimizer/translator change. The
  targeted audit inspected cache ownership, binding, Boolean interval composition,
  index links, aggregate eligibility and sorting, but did not independently
  reimplement every changed path.
- [ ] Schedule realistic disk/concurrency/soak validation if required by the
  release's target workload. No production dataset or latency budget was supplied.
- [ ] Track the seven inherited skipped tests separately: parallel fetch,
  exclusive disk scheduling, rebuild culture error, current-culture behavior,
  the slow issue-2127 reproduction, PredicateBuilder, and the flaky persisted
  multi-block vector case. They remain gaps, not successful tests.

The code fixes and reproducible checks increase confidence materially. The
remaining deployment and tradeoff decisions require workload requirements and
maintainer judgment; they should stay visible rather than being marked complete
because the automated suites pass.
