# Incremental query optimization measurements

These changes build on the shared query IR (`57a94728`). Each optimization has
its own commit, correctness tests, and before/after measurements against the
immediately preceding implementation. Ordinary LINQ and SQL use the query
optimizations automatically.

## Method

`tools/QueryOptimizationBenchmarks` runs complete queries on an in-memory database
of 20,000 documents, with indexes on `_id`, `Score`, and `City`. Every query fully
consumes and checksums its results. Database creation, inserts, indexing, and
warmup are outside timing. Each process records nine batches, elapsed time,
allocated bytes, Gen0 collections, and representative execution plans.

Measurements use production assemblies (`TestingEnabled=false`), .NET 8.0.30 on
Linux x64 / AMD Ryzen 9 3900X, pinned to CPU 2 with tiered compilation disabled.
Two processes per version run sequentially in before/after/after/before order;
tables pool their 18 samples and report medians. These are warm, memory-resident
workloads, not disk throughput claims. Raw data is in
[`benchmarks/query-optimization`](benchmarks/query-optimization).

Build each library revision to a separate output directory, then compile the
same harness against it:

```sh
dotnet build LiteDB/LiteDB.csproj -c Release -f net8.0 -p:TestingEnabled=false -o /tmp/opt-lib
dotnet build tools/QueryOptimizationBenchmarks -c Release -p:LiteDBAssembly=/tmp/opt-lib/LiteDB.dll -o /tmp/opt-bench
DOTNET_TieredCompilation=0 taskset -c 2 dotnet /tmp/opt-bench/QueryOptimizationBenchmarks.dll label or
python3 tools/QueryIrBenchmarks/compare.py --before before-1.json before-2.json --after after-1.json after-2.json
```

The optional final argument filters workload names by prefix; omit it to run
the full suite. An optional third argument scales iterations per batch (for
example, `label boolean-control 10` uses ten times as many). The `or` prefix also includes the `ordinary-*` control queries.

## 1. Equality disjunctions use indexed seeks

`Score == 1234 || Score == 17890` previously scanned all 20,000 documents.
The optimizer recognizes equalities on the same scalar indexed expression and
executes the equivalent IN seeks. It supports nested OR, reversed operands,
parameters, and deterministic expression indexes. Mixed fields/operators and
multikey ANY/ALL predicates retain their existing behavior. Seek values are
ordered and deduplicated using the database collation, preserving index-based
sorting and pagination.

| Complete query | Before µs | After µs | Time reduction | Before B/query | After B/query |
|---|---:|---:|---:|---:|---:|
| OR, LINQ | 31,276.40 | 64.82 | 99.8% (483×) | 40,879,384 | 54,254 |
| OR, SQL | 30,322.66 | 63.95 | 99.8% (474×) | 40,882,488 | 57,510 |
| Ordinary primary-key lookup, control | 41.68 | 40.62 | 2.5% | 33,652 | 33,676 |
| Ordinary combined predicate, control | 65.02 | 63.71 | 2.0% | 37,539 | 37,587 |

The large gain comes from replacing a full scan with two seeks. The control
queries have unchanged plans; their small timing differences are not evidence
of a general speedup. Focused disjunction, ordering, and shared-IR tests: 29 passed.

## 2. Intersect scalar index bounds

`Score >= 10000 && Score < 10010` previously scanned from 10000 through 20000,
loading documents to test the upper bound. The optimizer now intersects bounds
on the selected scalar index and removes the filters the bounded scan enforces.
Inclusive/exclusive endpoints, reversed comparisons, redundant bounds, separate
parameter documents, numeric types, and collation are preserved. ANY/ALL ranges
are excluded because separate elements may satisfy their bounds. Backtracking
at an inclusive range endpoint now also uses the database collation.

| Complete query | Before µs | After µs | Time reduction | Before B/query | After B/query |
|---|---:|---:|---:|---:|---:|
| Bounded range, LINQ | 14,453.61 | 80.76 | 99.4% (179×) | 19,485,064 | 57,523 |
| Bounded range, SQL | 14,329.03 | 73.40 | 99.5% (195×) | 19,486,400 | 58,947 |

This case returns ten documents. The gain depends on how much of the original
one-sided scan the other bound excludes. Range and disjunction tests: 14 passed.

## 3. Prune contradictory scalar constraints

The optimizer proves incompatible equalities/ranges on scalar paths using the
active collation, then supplies an empty input to the normal query pipeline.
This works without an index and preserves empty aggregate/group/count behavior.
Bound parameters are checked on each execution, including separate Where calls.
Multikey predicates and computed field expressions are excluded from the proof.

| Complete query | Before µs | After µs | Time reduction | Before B/query | After B/query |
|---|---:|---:|---:|---:|---:|
| Indexed impossible range | 14,274.38 | 38.01 | 99.7% (376×) | 19,465,812 | 21,771 |
| Unindexed incompatible equalities | 30,508.36 | 40.57 | 99.9% (752×) | 38,640,520 | 19,971 |

The workloads are `Score > 10010 && Score < 10000` and
`Name == "Person1" && Name == "Person2"`. These are deliberately impossible
queries: this optimization helps generated predicates and does not promise a
speedup for satisfiable queries. Optimizer and vector regression tests: 268 passed,
one existing skip.

## 4. Automatically reuse ordinary LINQ shapes

A bounded cache owned by each mapper reuses compiled logical templates for
structurally equivalent LINQ expressions. The workloads construct ordinary LINQ
queries with changing captured values on every iteration; none calls `Bind`.
Each call reevaluates and serializes its current values. Metadata guards handle
mapper changes, and structural keys retain no captured objects. Tests cover
nested bindings, closures, getter evaluation order, mutable metadata, live custom
serializers, enum settings, concurrent callers, reentrancy, and bounded storage.

This smaller effect uses three processes per version (27 batches, interleaved
before/after/after/before/before/after) after finalizing the cache implementation.

| Complete ordinary LINQ query | Before µs | After µs | Time reduction | Before B/query | After B/query |
|---|---:|---:|---:|---:|---:|
| Primary-key lookup | 38.03 | 34.83 | 8.4% | 33,676 | 32,451 |
| Combined indexed predicate | 63.28 | 52.03 | 17.8% | 38,363 | 34,895 |
| Indexed filter plus projection, five rows | 98.37 | 85.31 | 13.3% | 50,835 | 46,641 |

These are warm repeated shapes, including fresh expression trees and closures;
first use still translates. Structural indexer arguments, synthetic enum/DbRef
bindings, invoked lambdas, and oversized/unsupported shapes retain the existing
direct path. Explicit `Bind` can avoid the structural lookup as well. This is a
broad incremental improvement; unlike the earlier plan changes, it does not
reduce how many documents a query reads. Full .NET 8 suite: 933 passed, seven
existing skips.

## 5. Simplify constant guards before planning

An optional filter such as `!enabled || row.Score == 1234`, with `enabled = true`,
previously hid the usable Score index behind an OR. The shared optimizer evaluates
safe constant/parameter Boolean guards and exposes the remaining predicate.
Both frontends benefit, and guards are evaluated again for each binding. Volatile
expressions stay in the filter; conditional immutability now correctly requires
all three operands to be immutable. Short circuits avoid unreachable expressions,
and a right-hand absorbing constant does not suppress evaluation of its left.

| Complete query | Before µs | After µs | Time reduction | Before B/query | After B/query |
|---|---:|---:|---:|---:|---:|
| Optional filter, LINQ | 29,195.13 | 38.57 | 99.9% (757×) | 40,873,992 | 32,744 |
| Optional filter, SQL | 29,685.81 | 53.43 | 99.8% (556×) | 40,882,336 | 39,766 |

This measures enabling a selective filter, which changes a full scan into one
seek. Disabling the filter intentionally returns all rows and still requires a
scan. Focused simplification tests: 18 passed, including outer current paths and
empty vector queries; expression/shared-IR checks also passed. Existing pipeline ordering already filters before sorting/projection and
defers unnecessary includes, so no duplicate ordering rewrite was added.

## 6. Intersect IN and BETWEEN constraints before selecting an index

The planner now combines scalar equality, IN, range, and BETWEEN constraints
before comparing index costs. It removes only filters enforced by the selected
scan. Intersections use the active collation and current parameter values;
ANY/ALL predicates remain separate. Internal volatility metadata also prevents
hoisting changing functions nested inside MAP or array expressions.

| Complete query | Before µs | After µs | Time reduction | Before B/query | After B/query |
|---|---:|---:|---:|---:|---:|
| IN list plus range, SQL | 858,050.25 | 488.82 | 99.9% (1,755×) | 37,896,048 | 299,358 |
| Overlapping IN lists, SQL | 50,753.46 | 677.61 | 98.7% (75×) | 14,730,464 | 181,830 |
| Overlapping BETWEEN ranges, SQL | 33,332.30 | 81.42 | 99.8% (409×) | 41,543,632 | 60,278 |
| Combined Contains and range, LINQ | 976,855.58 | 966,851.26 | 1.0% | 42,244,816 | 42,244,760 |
| Primary-key lookup, control | 34.62 | 34.12 | 1.4% | 33,274 | 33,290 |
| Different-field predicate, control | 45.13 | 44.47 | 1.5% | 31,738 | 31,762 |

The IN workloads use 1,000 candidate keys and return eleven or six documents;
the BETWEEN workload returns ten. The combined LINQ workload exposes a separate
miss: a Boolean wrapper hides Contains from normalization, leaving its plan
unchanged at this step. Its small timing difference and the control differences
are not evidence of an improvement. SQL, string expressions, and LINQ with
separate Contains/range Where calls can use the new intersection.

Raw samples: `06-constraints-*`. All checksums match. Full .NET 8 and .NET 10
suites each pass 967 tests with seven existing skips; the Release solution builds.

## 7. Expose predicates inside Boolean identity comparisons

The optimizer removes Boolean identity comparisons such as `(predicate) = true`
and `(predicate) != false`. This exposes Contains when it appears inside an
ordinary LINQ conjunction, allowing the existing IN normalization and constraint
intersection to run. SQL uses the same rewrite. Changing Boolean parameters,
BSON type distinctions, short circuits, and negated comparisons retain their
semantics. ANY is now explicit node metadata; logical rewrites previously lost
it when its detection depended on generated expression text.

| Complete query | Before µs | After µs | Time reduction | Before B/query | After B/query |
|---|---:|---:|---:|---:|---:|
| Combined Contains and range, LINQ | 978,483.30 | 673.84 | 99.9% (1,452×) | 42,244,760 | 413,488 |
| Boolean-wrapped OR, SQL | 30,318.25 | 70.15 | 99.8% (432×) | 42,005,704 | 60,294 |
| Primary-key lookup, longer control | 37.90 | 35.94 | 5.2% | 33,290 | 33,290 |
| Different-field predicate, longer control | 49.54 | 47.44 | 4.2% | 31,762 | 31,762 |

The LINQ workload is the same case left unchanged by step 6, with a 1,000-key
candidate list and eleven returned documents. No API change or explicit Bind is
needed. Initial short control runs varied in both directions (including a 3.9%
slowdown for the point lookup), so the controls above use a fresh pair of runs
with ten times as many iterations per batch. Their allocations and plans are
unchanged; the timing variation does not establish a general speedup or regression.
All original short samples and the longer repeats are retained in `07-*`.

All checksums match within each comparison. Full .NET 8 suite: 981 passed, seven
existing skips. The Release solution builds all targets.

## 8. Build EXPLAIN documents only when requested

Every ordinary query previously built and discarded a BSON execution-plan
document. EXPLAIN queries built it twice. Removing that unused call avoids plan
formatting and allocation while keeping the requested EXPLAIN output identical.
This is a small execution-path change that benefits both frontends automatically.

| Complete query | Before µs | After µs | Time reduction | Before B/query | After B/query |
|---|---:|---:|---:|---:|---:|
| Primary-key lookup, LINQ | 36.28 | 36.76 | -1.3% | 33,290 | 30,970 |
| Primary-key lookup, SQL | 37.07 | 36.14 | 2.5% | 36,801 | 34,481 |
| Combined predicate, LINQ | 48.01 | 44.87 | 6.5% | 31,762 | 29,234 |
| Five-row projection, LINQ | 86.34 | 81.56 | 5.5% | 43,764 | 41,124 |
| EXPLAIN query | 22.28 | 20.60 | 7.5% | 16,217 | 13,897 |
| Full-scan control | 64,430.93 | 62,526.50 | 3.0% | 51,529,808 | 51,527,538 |

The two paired processes use five times the default iterations per batch. All
checksums and the EXPLAIN documents match. Point-query timings still vary on this
shared host; their small differences and the scan timing are inconclusive. The
allocation reduction is consistent: roughly 2.3–2.6 KB per query, or 6–8% in the
ordinary lookup/projection workloads. Raw measurements: `08-diagnostics-*`.

The full .NET 10 suite passes 981 tests with seven existing skips, and all Release
solution targets build. Existing query/EXPLAIN coverage validates this change.

## 9. Count matching rows directly from the index

Pure row COUNT/ANY projections now consume the index's deduplicated document
stream when no residual filter, sort, group, include, vector operation, or update
lookup is needed. Count/LongCount/Exists use this automatically. Multiple pure
COUNT/ANY fields share one traversal, and ANY-only queries stop at the first row
after the offset. Recognition uses the structured expression tree. Pagination,
null/missing scalar paths, aliases, empty inputs, and transaction safepoints keep
their existing behavior; other aggregates retain the document pipeline.

| Complete query | Before µs | After µs | Time reduction | Before B/query | After B/query |
|---|---:|---:|---:|---:|---:|
| Indexed range count, LINQ | 13,993.09 | 2,220.25 | 84.1% (6.3×) | 17,582,376 | 5,204,049 |
| Indexed range count, SQL | 13,387.05 | 2,270.31 | 83.0% (5.9×) | 18,861,608 | 5,203,989 |
| Full collection count, LINQ | 10,666.49 | 4,213.71 | 60.5% (2.5×) | 18,943,592 | 10,098,376 |
| Two counts plus ANY, SQL | 25,191.02 | 2,282.48 | 90.9% (11.0×) | 33,831,152 | 5,211,517 |
| Indexed Exists, LINQ | 55.50 | 51.97 | 6.4% | 36,123 | 34,111 |
| Empty indexed Exists, LINQ | 58.23 | 56.84 | 2.4% | 47,828 | 47,296 |
| Count with residual filter, control | 26,457.38 | 25,961.55 | 1.9% | 25,910,432 | 25,910,500 |
| Primary-key lookup, control | 34.48 | 33.67 | 2.3% | 30,970 | 30,978 |

The range matches 10,001 documents; the full count traverses all 20,000 index
entries. Counts still traverse the index and do not use cached row totals.
Exists already stopped early, so avoiding one lookup gives a much smaller gain.
Small control timing differences remain within the shared host's observed
variation. Both versions use twice the default iterations per batch; all
consumed-result checksums match. Raw measurements: `09-aggregate-*`.

Full .NET 8 suite: 1,001 passed; the final 22-case aggregate suite (including two
additional transaction/update cases) also passes. Full .NET 10 suite: 1,003 passed.
Both full runs have seven existing skips; all Release solution targets build.

## 10. Apply bounds before constructing IN sets

Constraint planning now evaluates the complete bounds before building sorted
candidate sets. Values excluded by those bounds never enter the sets, and
multiple IN lists start with the shortest list. Equality constraints are applied
as bounds while preserving the previous equality-seek behavior. Final scans and
result ordering remain the same.

| Complete query | Before µs | After µs | Time reduction | Before B/query | After B/query |
|---|---:|---:|---:|---:|---:|
| Contains plus range, LINQ | 717.53 | 366.59 | 48.9% | 409,519 | 354,359 |
| IN plus range, SQL | 514.26 | 192.94 | 62.5% | 295,985 | 240,909 |
| Overlapping IN lists, SQL | 685.40 | 655.22 | 4.4% | 178,833 | 178,317 |
| BETWEEN control, SQL | 77.03 | 70.62 | 8.3% | 57,321 | 57,409 |
| Primary-key lookup, control | 36.70 | 32.54 | 11.3% | 30,978 | 30,978 |
| Different-field predicate, control | 43.14 | 42.70 | 1.0% | 29,242 | 29,242 |

The selective IN/range case rejects 989 of 1,000 keys before set construction,
saving roughly 55 KB per complete query. The control timings again show host
variation (particularly one slower baseline process), so the small IN/IN and
control timing differences are inconclusive. The large selective-IN improvement
appears in both paired processes and reduces measured allocation as well.

Both versions use five times the default iterations; every checksum matches.
Raw measurements: `10-sets-*`. Existing optimizer tests, including randomized
intersections and collation/numeric cases: 95 passed. All Release targets build.

## 11. Reuse built-in Count/Exists expression templates

Count/LongCount/Exists previously reparsed their fixed SELECT expressions on
every invocation. They now construct those logical templates once through the
shared factories, then bind independent parameter documents. This independence
matters because GROUP BY writes its key parameter during execution. The helpers
still restore the original projection on success or failure and select physical
indexes for each query.

| Complete query | Before µs | After µs | Time reduction | Before B/query | After B/query |
|---|---:|---:|---:|---:|---:|
| One-row Count, LINQ | 51.18 | 29.53 | 42.3% | 35,963 | 29,746 |
| One-row LongCount, LINQ | 51.28 | 29.90 | 41.7% | 35,963 | 29,746 |
| Indexed Exists, LINQ | 50.74 | 29.51 | 41.8% | 34,051 | 27,850 |
| Empty indexed Exists, LINQ | 56.35 | 34.57 | 38.6% | 47,236 | 41,034 |
| One-row Count with residual filter | 76.59 | 52.72 | 31.2% | 44,485 | 38,490 |
| SQL count, control | 46.37 | 43.05 | 7.2% | 36,082 | 36,082 |
| Primary-key lookup, control | 34.18 | 32.29 | 5.5% | 30,978 | 30,978 |

Each process records nine batches of 4,000 complete queries. Both before/after
pairs show the aggregate-helper improvement, with about 6 KB fewer allocated
bytes per invocation. Controls have unchanged allocations and plans; their timing
variation is not attributable to this change. Raw samples: `11-helpers-*`; all
checksums match.

Tests cover canonical expression parity, no tokenization within an existing
snapshot, independent grouped/concurrent bindings, and projection restoration
after exceptions. Full .NET 8 suite: 1,007 passed, seven existing skips. All Release
solution targets build.

## 12. Parse persisted index expressions only when needed

Opening a collection snapshot previously parsed every persisted index expression,
even when the query only needed existing index keys and canonical expression
text. The metadata reader now defers expression construction until evaluation is
needed, retaining it on that metadata instance. New index definitions still
validate eagerly. Writes and vector evaluation request the expression normally;
there is no global metadata cache or stale physical-plan reuse.

| Complete query | Before µs | After µs | Time reduction | Before B/query | After B/query |
|---|---:|---:|---:|---:|---:|
| Primary-key lookup, LINQ | 33.34 | 26.20 | 21.4% | 30,978 | 27,257 |
| Primary-key lookup, SQL | 34.89 | 29.65 | 15.0% | 34,489 | 30,769 |
| Combined predicate, LINQ | 44.29 | 38.61 | 12.8% | 29,242 | 25,521 |
| Five-row projection, LINQ | 80.52 | 73.73 | 8.4% | 41,131 | 37,410 |
| One-row Count, LINQ | 31.48 | 23.72 | 24.7% | 29,746 | 26,025 |
| Indexed Exists, LINQ | 31.40 | 23.96 | 23.7% | 27,850 | 24,128 |
| Update, control | 41.59 | 38.90 | 6.5% | 47,844 | 46,612 |
| Full scan, control | 61,863.06 | 60,586.96 | 2.1% | 51,527,536 | 51,523,501 |

These ordinary queries save about 3.7 KB each in a collection with three indexes.
Both process pairs show the read-query gains; the small full-scan difference is
inconclusive. Updates still evaluate secondary index expressions, while avoiding
unused expression construction. Query batches contain 4,000 iterations (2,000
for projection); the update and scan controls use 1,000 and three respectively.
Raw samples: `12-metadata-*`. All query/update checksums match.

Focused metadata tests cover parser-free ordinary reads, lazy expression reuse,
index maintenance, and eager validation of new definitions. Full .NET 10 suite:
1,011 passed, seven existing skips. All Release solution targets build.

## Combined result and practical priority (steps 1–5)

A separate complete-suite comparison runs the post-IR baseline against all five
optimizations together. Two processes per version use the same 18-batch method;
all consumed-result checksums match. The raw `all-before-*` / `all-after-*` files
include the execution plans and every workload, not just the strongest cases.

| Complete query | Baseline µs | All changes µs | Time reduction |
|---|---:|---:|---:|
| Equality OR, LINQ | 29,784.99 | 49.20 | 99.8% |
| Optional filter, LINQ | 29,871.67 | 38.90 | 99.9% |
| Bounded range, LINQ | 14,545.73 | 69.32 | 99.5% |
| Impossible indexed range | 14,498.15 | 28.36 | 99.8% |
| Impossible unindexed equalities | 29,437.24 | 29.20 | 99.9% |
| Ordinary primary-key lookup | 39.73 | 37.69 | 5.1% |
| Ordinary combined predicate | 61.43 | 55.42 | 9.8% |
| Ordinary indexed projection | 101.75 | 90.40 | 11.2% |
| Full-scan control | 60,735.77 | 61,149.38 | -0.7% |

The full-scan control is effectively unchanged. Timings on this shared host vary,
so per-step percentages should not be multiplied together. The separate cache
measurements isolate its benefit; combined measurements also include the added
optimizer checks. Cold queries, disk-bound execution, and different selectivity
can have very different results.

The best practical return is avoiding unnecessary reads: indexed OR seeks,
bounded ranges, and simplifying optional guards. Contradiction pruning is cheap
but benefits impossible queries only. Automatic LINQ caching benefits repeated
ordinary shapes more broadly, with smaller gains. Further filter ordering would
need a cost model and purity rules; the pipeline already puts filters before
sorting and projection. A source generator, physical-plan cache, and specialized
materializers remain separate work.

## Validation

- Release solution build with `TestingEnabled=true`: all targets build.
- Full `LiteDB.Tests` with `tests.runsettings`: 1,007 passed on .NET 8 at step 11;
  1,011 passed on .NET 10 at step 12; four focused metadata tests also pass on .NET 8. Each full
  run has seven existing skips.
- Reproduction-runner tests: 18 passed.
- Vector file compatibility: ordinary v8 round trips and promoted vector-file
  rejection by LiteDB 5.0.21 pass for plain and encrypted files.
- C# size checks and whitespace checks pass; new C# files remain under 300 lines.
- Each benchmark comparison checks matching consumed-result checksums.
