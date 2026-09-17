# Incremental query optimization measurements

These changes build on the shared query IR (`57a94728`). Each optimization has
its own commit, correctness tests, and before/after measurements against the
immediately preceding implementation. Ordinary LINQ and SQL use the query
optimizations automatically.

See the [fresh cumulative comparison through step 19](query-optimization-overall.md)
for the combined effect, including ordinary queries and scan controls.

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

## 13. Fix replay addresses for index-only queries

Aggregate and sort regression testing exposed an existing correctness bug:
IndexLookup returned a data-block address, then treated that address as an index
node during replay. It now returns the index node's position, matching its own
reload method. Multiple aggregates, constant grouping, and computed sorting can
therefore replay index-only values correctly.

These workloads previously failed with `page type must be index page`; there is
no valid baseline duration and no speedup claim. Each reproducer runs in an
isolated database of 20,000 documents because the baseline error faults its engine.
Database setup is outside timing. Successful timings pool two nine-batch processes:

| Complete query | Before | After µs | After B/query |
|---|---|---:|---:|
| SUM plus MAX over indexed IDs | Fails | 21,273.14 | 36,514,744 |
| Group by a constant, then Count | Fails | 32,043.04 | 36,700,584 |
| Computed two-key sort, limit ten | Fails | 67,961.80 | 25,628,075 |

Raw `13-replay-*` files preserve the three baseline errors and successful results.
After-query checksums are verified against independently calculated expected sums,
counts, and ordered IDs. Four focused regressions include collated/null secondary
keys. Full .NET 8 suite: 1,015 passed, seven existing skips; all Release targets build.

## 14. Use a shared leading guard across OR branches

For `(City = @a AND ...) OR (City = @b AND ...)`, matching leading equalities
can provide an indexed seek when their current values are equal under the active
collation. The full original OR remains a residual filter. Extraction is limited
to the first condition of every branch, preserving short circuits around throwing
or volatile expressions. Includes, multikey guards, and oversized analyses fall
back to the existing plan.

| Complete query | Before µs | After µs | Time reduction | Before B/query | After B/query |
|---|---:|---:|---:|---:|---:|
| Shared city guard, LINQ | 33,415.05 | 128.84 | 99.6% (259×) | 43,132,160 | 96,339 |
| Shared city guard, SQL | 33,610.87 | 136.88 | 99.6% (246×) | 43,138,056 | 102,259 |
| Primary-key lookup, control | 26.74 | 24.94 | 6.7% | 27,257 | 27,257 |
| Different-field predicate, control | 36.71 | 36.85 | -0.4% | 25,617 | 25,677 |

The OR branches combine the same City equality with different Name/Score filters.
The seek narrows 20,000 candidate documents to twenty, then evaluates the original
OR. Separate LINQ parameter slots are compared by their current values. The small
control differences do not establish a general gain. Raw samples: `14-common-*`;
all checksums match.

Eleven focused tests cover parameter rebinding, reversed operands, collation,
ordering/pagination, short circuits, includes, and fallback limits. Full .NET 10
suite: 1,026 passed, seven existing skips. All Release solution targets build.

## 15. Reduce automatic LINQ cache collision churn

The mapper-local 256-template cache now uses 64 buckets with up to four entries
each. Bucket selection mixes all shape-hash bits: repeated expression nodes
otherwise produce patterned low bits. Structural equality and mapper guards
still validate every hit. Immutable bucket arrays are published atomically;
concurrent publication avoids duplicate shapes, and full buckets evict an older
entry without growing the bound or retaining closures.

| Complete query | Before µs | After µs | Time reduction | Before B/query | After B/query |
|---|---:|---:|---:|---:|---:|
| Cycle through 128 LINQ shapes | 79.60 | 44.69 | 43.9% | 61,744 | 41,514 |
| Single shape, primary-key control | 25.94 | 26.80 | -3.3% | 27,257 | 27,257 |
| Single shape, combined control | 36.89 | 36.70 | 0.5% | 25,521 | 25,521 |

The workload cycles through 128 fixed generated Boolean shapes, all selecting
the same indexed row with ordinary Where calls. It models generated-query churn;
it does not imply a 44% gain for applications using only a few stable shapes.
Metadata hashes vary across processes, so the raw report also records retained
cache entries. Both final process pairs improve the many-shape workload while
allocations fall 33%. Single-shape control differences remain small relative to
observed host variation, and their allocations are unchanged.

An initial four-entry-bucket version without hash mixing did **not** help
(77.47 → 81.43 µs, 5.1% slower). Its raw `15-initial-buckets-*` samples are retained;
that intermediate implementation is not included in the commit. Final samples
are `15-cache-*`; each comparison has matching consumed-result checksums.

Twelve focused cache tests pass, including collisions, bounded eviction, and
concurrent duplicate publication. The pre-mixing full .NET 8 run passed 1,029
tests with seven existing skips; the final mixing change passes the cache suite
and all Release solution targets build. Existing mutable-mapper, binding,
reentrancy, concurrency, and closure-lifetime tests remain covered.

## 16. Retain only the requested keys for small sorted pages

Residual ORDER BY queries with a positive limit and `offset + limit <= 1024`
now use a bounded maximum heap. It retains the best requested keys and reload
addresses, then sorts only those retained entries. Every input key is still
evaluated and size-checked; this does not skip the input scan. Comparisons preserve
collation, mixed directions, and input-order ties. Larger/unbounded requests keep
the existing sorter with disk spilling.

| Complete query | Before µs | After µs | Time reduction | Before B/query | After B/query |
|---|---:|---:|---:|---:|---:|
| Sort by unindexed Name, first ten | 60,094.65 | 30,080.69 | 49.9% | 39,632,048 | 37,522,392 |
| City/Score mixed sort, page of ten, LINQ | 91,930.56 | 42,540.81 | 53.7% | 46,051,304 | 42,810,848 |
| Same mixed sort, SQL | 92,210.80 | 42,181.95 | 54.3% | 46,052,387 | 42,811,968 |
| Computed index-only sort, first ten | 66,256.61 | 25,196.45 | 62.0% | 25,179,889 | 21,945,984 |
| Index-provided ordering, control | 47.26 | 49.18 | -4.1% | 30,337 | 30,337 |
| Primary-key lookup, control | 26.62 | 26.83 | -0.8% | 27,257 | 27,257 |

These queries inspect 20,000 input rows and return ten; allocation remains
substantial because document/key evaluation still happens for each input.
Index-provided ordering bypasses sorting entirely, so that control's timing
variation is inconclusive and its allocations are unchanged. All consumed-result
checksums match. Raw measurements: `16-topn-*`.

Tests compare full and bounded sorting, randomized predicates, pagination around
the capacity boundary, mixed directions, null/collated keys, stable ties, includes,
index-only aggregate replay, and discarded invalid keys. Exact vector coverage
checks both small in-memory rankings and larger disk-spilling rankings. Full
.NET 10 suite: 1,044 passed, seven existing skips; all Release targets build.

## 17. Avoid document deduplication for the primary index

Primary-index traversals have one scalar entry per document; IN seeks already
deduplicate collated keys before seeking. They now skip the extra document-address
set. Secondary and multikey index scans keep their existing deduplication. This
reduces traversal overhead without changing index choice or query semantics.

| Complete query | Before µs | After µs | Time reduction | Before B/query | After B/query |
|---|---:|---:|---:|---:|---:|
| Full primary count | 4,010.56 | 2,870.02 | 28.4% | 10,088,256 | 8,354,936 |
| Primary range count | 2,297.02 | 1,708.56 | 25.6% | 5,198,552 | 4,360,976 |
| Primary range, materialized documents | 30,258.55 | 30,103.57 | 0.5% | 21,027,325 | 20,189,749 |
| Primary-key lookup | 25.50 | 25.23 | 1.1% | 27,257 | 26,985 |
| Secondary count, control | 2,211.59 | 2,187.66 | 1.1% | 5,193,776 | 5,193,776 |
| Primary scan with residual filter | 58,048.06 | 56,721.64 | 2.3% | 51,523,533 | 49,790,243 |

The count cases expose index traversal cost, making this reduction measurable.
Materialization dominates the document workloads; their small timing differences
are inconclusive, though the allocation savings are consistent. Raw samples:
`17-primary-*`. Every consumed-result checksum matches.

Five focused tests cover duplicate IN/OR keys, collation, ranges, and retained
multikey deduplication. Full .NET 8 suite: 1,049 passed, seven existing skips;
all Release solution targets build.

## 18. Remove unnecessary work during index selection

Index matching now searches candidate metadata directly, preserving the previous
left-operand preference and ANY/ALL rules. Scalar bounds are evaluated directly
instead of through enumerable adapters. Preferred full scans no longer parse an
index expression into a node that cannot consume any WHERE predicate. These
changes reduce planning allocations without changing selected plans.

| Complete query | Before µs | After µs | Time reduction | Before B/query | After B/query |
|---|---:|---:|---:|---:|---:|
| Primary-key lookup, LINQ | 25.59 | 22.83 | 10.8% | 26,985 | 25,809 |
| Combined predicate, LINQ | 36.83 | 31.70 | 14.0% | 25,521 | 23,240 |
| Reversed primary equality, SQL | 28.97 | 26.17 | 9.6% | 30,497 | 29,321 |
| Index-provided order, first ten | 45.53 | 43.45 | 4.6% | 30,337 | 29,016 |
| Full primary count, control | 3,008.16 | 3,054.41 | -1.5% | 8,354,936 | 8,353,328 |
| Full scan, control | 58,352.16 | 58,688.56 | -0.6% | 49,790,243 | 49,789,568 |

Point and combined queries allocate roughly 1.2–2.3 KB less per execution.
The scan/count controls are effectively unchanged; their per-query planning
cost is small relative to traversal. All checksums match. Raw measurements:
`18-planning-*`. Full .NET 10 suite: 1,049 passed, seven existing skips; all Release
solution targets build. Existing index, ANY/ALL, expression, and plan parity tests
cover the preserved selection behavior.

## 19. Avoid unused source arrays during expression evaluation

Single-document expression execution now creates a singleton source array only
when the expression uses the source stream. The root/current document arguments
are unchanged. Source-dependent expressions retain their singleton, and the
no-root scalar overload retains its historical empty-input semantics. This removes
small per-row allocations from ordinary filters, projections, and sort expressions.

| Complete query | Before µs | After µs | Time reduction | Before B/query | After B/query |
|---|---:|---:|---:|---:|---:|
| Filtered document scan | 58,023.44 | 58,454.80 | -0.7% | 49,789,570 | 48,794,014 |
| Computed projection over all rows | 65,801.74 | 65,647.91 | 0.2% | 35,099,138 | 34,459,085 |
| Computed two-key top-N | 39,040.77 | 37,730.65 | 3.4% | 41,872,080 | 40,591,760 |
| Count with residual expression | 28,286.01 | 28,380.58 | -0.3% | 34,129,184 | 33,489,184 |
| Index-only count, control | 2,143.77 | 2,346.07 | -9.4% | 5,192,600 | 5,192,576 |
| Primary lookup, control | 23.93 | 24.58 | -2.7% | 25,809 | 25,753 |

The demonstrated benefit here is allocation reduction, **not a proven latency
improvement**: 0.64–1.28 MB less per complete scan/projection/sort query (1.8–3.1%).
Host variation affected controls too, particularly one after process's index-only
count, whose per-row execution does not evaluate expressions. Timing differences
in this comparison are inconclusive. Raw samples: `19-source-*`; all checksums
match.

Ten differential tests compare implicit/explicit singleton sources, including
nested MAP/FILTER/SORT, aggregates, rebinding, and no-root/null overload semantics.
Full .NET 8 suite: 1,059 passed, seven existing skips; all Release targets build.

## 20. Reuse closure-free evaluators for captured LINQ helpers

A cache hit still compiled and dynamically invoked a fresh delegate for captured
calls such as `x => x.Id == provider.GetId()`, including calls underneath member
access. Templates now prepare an evaluator with every constant replaced by a
position in the **current** expression tree. It retains metadata, never a caller's
closure, object, or literal value. Compilation is lazy and shared across callers;
ordinary captured fields/properties retain their existing reflection path.

| Complete query | Before µs | After µs | Time reduction | Before B/query | After B/query |
|---|---:|---:|---:|---:|---:|
| Lookup using a captured method | 165.90 | 24.64 | 85.1% | 30,922 | 26,573 |
| Lookup using a member of a method result | 162.86 | 25.86 | 84.1% | 31,091 | 26,733 |
| Range count using two captured methods | 284.47 | 29.10 | 89.8% | 38,327 | 29,673 |
| Ordinary captured-field lookup, control | 26.00 | 25.36 | 2.4% | 28,123 | 28,123 |
| Translate with a fresh mapper, then execute | 1,108.06 | 1,117.89 | -0.9% | 89,961 | 91,869 |

Two processes per version, before/after/after/before, use changing helper values
and consume every result. Comparison files are `20-binding-before-2/3` against
`20-binding-after-1/2`. Checksums match. The fresh-mapper case includes constructing
and mapping a new mapper on every complete query; it allocates about 1.9 KB more
to prepare later reuse. Its timing and the field control are effectively unchanged.
The first reused call pays the one-time lazy compilation cost, outside these warm
measurements; subsequent calls avoid repeated compilation.

An initial eager implementation made fresh-mapper queries 24% slower. It was
rejected in favor of lazy compilation; `20-binding-before-1` and
`20-binding-initial-after-1` preserve that experiment separately.

Tests cover independent closures, literal occurrences (including shared nodes in
the first tree), initializers, nullable values, live serializers, exception chains,
evaluation order/count, reentrancy, concurrency, and garbage collection of both
unused and compiled evaluator inputs. Ambiguous reused binding nodes and nested
member/list initializer bindings retain uncached translation. The full .NET 8
suite passes 1,073 tests with seven existing skips; all Release targets build.

## 21. Reuse nested-expression source arrays across elements

MAP, FILTER, SORT, and bracket filters previously allocated a singleton source
array for each input element. They now create one per enumeration when the nested
expression uses its source, or use the shared empty array otherwise. Parameterized
array indexing also skips its unused source allocation. Root, current element,
parameters, deferred execution, and collation are preserved.

These complete queries use **4,000 documents with 32 integer array elements each**,
plus an Offset field. The ordinary 20,000-row collection is present but unused by
these workloads. Setup is outside timing, and every query consumes all results.

| Complete query | Before µs | After µs | Time reduction | Before B/query | After B/query |
|---|---:|---:|---:|---:|---:|
| LINQ nested projection | 71,619.27 | 71,407.91 | 0.3% | 54,931,157 | 50,867,074 |
| LINQ nested filter and projection | 82,114.79 | 80,506.99 | 2.0% | 62,724,353 | 55,172,069 |
| SQL bracket filter | 41,909.06 | 41,245.63 | 1.6% | 35,350,744 | 31,286,840 |
| SQL nested sort | 53,387.86 | 52,182.77 | 2.3% | 37,685,632 | 33,621,632 |
| SQL MAP with COUNT(*) in its selector | 51,459.83 | 50,961.76 | 1.0% | 58,712,240 | 54,776,240 |
| Read array documents, control | 33,582.01 | 33,390.47 | 0.6% | 20,462,784 | 20,462,784 |

The clear benefit is **6.7–12.0% fewer allocated bytes**, about 3.9–7.6 MB per
complete query. The small timing changes are not strong evidence of a latency
gain on this shared host. All checksums match; raw files are `21-nested-*`.
Eleven new tests cover source-dependent selectors, root/current references,
rebinding, repeated enumeration, empty arrays, and collation. Full .NET 10 suite:
1,084 passed, seven existing skips; all Release targets build.

## 22. Execute scalar MAP selectors without per-element enumerators

The shared IR already knows whether a MAP selector returns a single value.
Scalar selectors now execute directly, avoiding a wrapper enumerator for each
input element. Enumerable selectors retain their flattening behavior; arrays
returned by scalar selectors remain single values. This benefits ordinary LINQ
nested projections and SQL MAP without API changes.

The same 4,000-document / 32-element dataset from step 21 is used, with an added
enumerable-selector flattening control. Measurements compare against step 21.

| Complete query | Before µs | After µs | Time reduction | Before B/query | After B/query |
|---|---:|---:|---:|---:|---:|
| LINQ nested projection | 71,084.06 | 62,413.82 | 12.2% | 50,867,114 | 34,482,658 |
| LINQ nested filter and projection | 79,157.82 | 72,360.29 | 8.6% | 55,172,109 | 41,091,736 |
| SQL bracket filter, control | 40,404.37 | 41,008.85 | -1.5% | 31,286,744 | 31,286,744 |
| SQL nested sort, control | 52,361.91 | 53,124.13 | -1.5% | 33,621,632 | 33,621,632 |
| SQL MAP with COUNT(*) in its selector | 51,501.60 | 43,030.93 | 16.4% | 54,776,240 | 38,392,312 |
| SQL enumerable MAP, control | 93,972.74 | 94,023.08 | -0.1% | 86,746,240 | 86,746,240 |
| Read array documents, control | 33,287.94 | 33,559.91 | -0.8% | 20,462,784 | 20,462,784 |

Affected queries allocate **25.5–32.2% fewer bytes**, about 14.1–16.4 MB less per
complete query. Controls retain identical allocation counts; their small timing
differences are within host variation. All checksums match. Raw files: `22-map-*`.
Nine new tests cover nulls, scalar array/document values, enumerable and nested
flattening, source aggregates, deferred failures, and disposal after early exit.
Full .NET 8 suite: 1,093 passed, seven existing skips; all Release targets build.

## 23. Release original parameter payloads from nested templates

Nested evaluators already receive current parameters explicitly, but their cached
BsonExpression objects still retained the original parameter document. Rebinding
could therefore keep large, unused first-use values alive through expression
trees, compiled delegates, and automatic LINQ templates. Factories now embed
unbound copies with no fallback parameter document. Public Bind still requires a
non-null binding, and explicitly null execution parameters keep their errors.

A dedicated **complete-query memory workload** executes 128 projections against a
one-document collection. It creates 64 distinct templates, each first bound to
10,000 integer keys and then rebound to one key. It retains the 64 rebound
templates, releases the original bindings, forces collection, and checks both
live heap growth and weak references to the original arrays. Every query is fully
consumed; both versions produce checksum 256.

| After 128 complete queries | Before | After |
|---|---:|---:|
| Original parameter arrays still alive | 64 / 64 | 0 / 64 |
| Managed live-heap growth | 44,667,360 B | 435,888 B |

That is **44.2 MB less retained managed memory** in this workload (99.0% less
live-heap growth). It does not free values an application intentionally retains,
measure native/JIT memory, or establish a general latency improvement. These
numbers are medians of two fresh production processes per version, run in
before/after/after/before order. Raw files: `23-lifetime-before-*` and
`23-lifetime-after-*`. `23-lifetime-initial-*` preserves an earlier implementation
using empty fallback documents, replaced to preserve explicitly null behavior.

Build `tools/QueryParameterLifetimeBenchmarks` against each production assembly
using `-p:LiteDBAssembly=/tmp/opt-lib/LiteDB.dll -o /tmp/lifetime-bench`, then run:

```sh
DOTNET_TieredCompilation=0 taskset -c 2 dotnet /tmp/lifetime-bench/QueryParameterLifetimeBenchmarks.dll label
```

The ordinary nested-query harness also compares step 22 against this change,
using the existing 4,000-document / 32-element dataset and eighteen batches per
version. Checksums match. Selected results follow; raw `23-throughput-*` files
include all seven workloads, allocations, and individual batches.

| Complete query | Before µs | After µs | Before B/query | After B/query |
|---|---:|---:|---:|---:|
| LINQ nested projection | 62,460.56 | 61,554.84 | 34,482,730 | 34,482,658 |
| LINQ nested filter and projection | 71,430.79 | 71,245.37 | 41,091,808 | 41,091,847 |
| SQL bracket filter | 41,159.25 | 40,462.28 | 31,286,744 | 31,287,424 |
| SQL MAP with source aggregate | 41,835.47 | 41,311.98 | 38,392,312 | 38,393,012 |
| Read array documents, control | 33,080.30 | 33,058.39 | 20,462,784 | 20,462,784 |

Timing is effectively unchanged. SQL parsing allocates small additional template
copies (roughly 0.1–0.7 KB/query in these cases); automatic LINQ cache hits do not
repeat that work. Six retention regression cases failed before the fix. Twelve
new tests now cover nested MAP/FILTER/SORT/indexing, recursive templates, the
first serialized LINQ array, current bindings, and null-parameter errors. Full
.NET 8 and .NET 10 suites: 1,105 passed each, seven existing skips; all Release
targets build.

## 24. Skip redundant document deduplication for unique secondary indexes

Unique secondary indexes, like the primary index, have one key/node per document:
index creation rejects unique multikey expressions. Their scans can bypass the
per-query address set, while IN continues to deduplicate seek values using the
active collation. Non-unique secondary indexes retain their existing behavior.

This comparison uses another 20,000-document collection with the same Row data,
a unique Score index, and a non-unique City index. Complete queries compare step
23 with this change; setup and index creation are outside timing.

| Complete query | Before µs | After µs | Time reduction | Before B/query | After B/query |
|---|---:|---:|---:|---:|---:|
| Unique-index range count | 2,094.99 | 1,608.22 | 23.2% | 5,209,768 | 4,372,264 |
| Unique-index full count | 4,231.92 | 3,396.26 | 19.7% | 10,426,632 | 8,693,952 |
| Covered range projection | 10,276.52 | 9,433.50 | 8.2% | 11,102,720 | 10,265,224 |
| Materialized document range | 29,530.68 | 28,363.14 | 4.0% | 20,718,650 | 19,881,150 |
| Non-unique index, control | 31.24 | 31.32 | -0.3% | 32,200 | 32,256 |
| Primary-index count, control | 2,864.28 | 2,861.32 | 0.1% | 8,357,872 | 8,357,872 |

Counts allocate about 16% fewer bytes. All checksums match; controls are effectively
unchanged. Raw files are `24-unique-*`, with representative count/projection plans.
Six tests cover duplicate IN/OR values, order/pagination, collation, scalar array
keys, rejection of unique multikey indexes, persisted metadata, updates, and
removal. Existing multikey deduplication tests also pass. Full .NET 10 suite:
1,111 passed, seven existing skips; all Release targets build.

## 25. Use scalar IR metadata to avoid redundant index deduplication

A scalar expression matching the stored index definition proves one key per
document even when the index is non-unique. The planner now carries that proof
into predicate, combined-constraint, disjunction, and explicit ordering/grouping
scans. It does not parse catalog expressions or add a persistent format flag.
Multikey expressions and preferred-field fallbacks without that proof keep
address deduplication. Repeated keys belonging to different documents still all
produce results.

The normal 20,000-row dataset has non-unique Score and City indexes. A separate
20,000-row control collection has two Tags per document and a Tags[*] index.
Comparisons are against step 24, with complete consumption and matching checksums.

| Complete query | Before µs | After µs | Time reduction | Before B/query | After B/query |
|---|---:|---:|---:|---:|---:|
| Non-unique scalar range count | 2,056.56 | 1,589.12 | 22.7% | 5,192,576 | 4,355,056 |
| Non-unique scalar full count | 4,167.03 | 3,373.04 | 19.1% | 10,422,232 | 8,688,984 |
| Covered scalar ordering | 19,534.21 | 18,042.57 | 7.6% | 21,876,480 | 20,143,176 |
| Count twenty documents sharing a City key | 30.94 | 29.69 | 4.0% | 32,288 | 30,640 |
| Primary lookup, short control | 21.97 | 23.36 | -6.3% | 25,753 | 25,753 |
| Multikey count, control | 8,506.57 | 8,450.38 | 0.7% | 19,723,672 | 19,723,672 |
| Full scan, control | 56,326.97 | 55,924.53 | 0.7% | 48,794,016 | 48,794,071 |

The short primary control prompted a separate comparison with ten times as many
iterations: **23.31 → 22.30 µs**, with identical 25,752 B/query. This reversal is
not evidence of a primary-lookup gain; neither run establishes a stable change
there. Both measurements are retained (`25-scalar-*` and `25-control-*`). Counts
and covered ordering show consistent improvements and remove the expected address
set allocations; multikey and scan controls remain effectively unchanged.

Nine new tests cover repeated keys, IN/OR/ranges and reversed operands, computed
keys, scalar ordering, multikey fallback, scalar array keys, and conservative
preferred-field handling. Full .NET 8 suite: 1,120 passed, seven existing skips;
all Release targets build.

## 26. Match literal field indexes using canonical escaped paths

The planner constructed preferred-field and covered-lookup identities with raw
`"$." + fieldName`. A literal field such as `Tags[*]`, `Nested.Value`, or `Score+1`
could match an unrelated multikey, nested, or computed index and return its keys
as the requested field's values. Both lookups now use the shared path formatter.
Whole-document field markers remain excluded from field-index matching.

Six regression cases fail before the change; the tests also cover correctly
escaped indexes, quotes, backslashes, numeric/Unicode names, and whole-document
reads. Those wrong-result cases are correctness evidence, **not speedup claims**.

Separate performance probes use 20,000 documents with a literal `Score.Value`
field and a 200-character payload. Only the correct literal index exists, so both
versions return the same results; the change enables preferred and covered use
of that index. All checksums match.

| Complete query | Before µs | After µs | Time reduction | Before B/query | After B/query |
|---|---:|---:|---:|---:|---:|
| Full literal-field projection | 84,387.10 | 64,269.95 | 23.8% | 46,928,248 | 33,012,555 |
| Filtered literal-field projection | 42,563.47 | 32,667.61 | 23.2% | 23,661,819 | 16,003,848 |
| Count of the literal field | 2,783.09 | 3,889.35 | -39.7% | 8,365,584 | 9,770,232 |
| Ordinary indexed projection, control | 65.45 | 65.21 | 0.4% | 35,586 | 35,642 |
| Primary lookup, control | 23.67 | 22.64 | 4.3% | 25,753 | 25,809 |

The count regression is real: recognizing the preferred non-unique index switches
from the primary scan to a preferred scan that still tracks duplicate document
addresses at this step. Its scalar-path proof is handled separately from the
field-identity correction. Control differences do not establish general gains.
Raw files: `26-escaped-*`. Initial samples (`26-initial-escaped-*`) used a different
constant-array count control; the final table uses the corrected literal-field
reference `COUNT(*.@.["Score.Value"])`, with its plan verified as a row aggregate.

Twelve new cases plus the existing scalar-index tests pass. Full .NET 10 suite:
1,132 passed, seven existing skips; all Release targets build.

## 27. Carry scalar field proofs into preferred index scans

After canonical escaping distinguishes literal fields from executable paths, a
preferred root-field index is also known to produce one key per document. Its
full scan now carries the same scalar proof as explicit predicates and ordering.
This avoids the address set for ordinary preferred projections/counts and repairs
the count slowdown exposed by step 26. Field markers and multikey paths remain
excluded from this proof.

The literal-field dataset and controls from step 26 are reused, with two added
ordinary preferred-field workloads on the main 20,000-row collection. Comparisons
are against step 26; every result is consumed and all checksums match.

| Complete query | Before µs | After µs | Time reduction | Before B/query | After B/query |
|---|---:|---:|---:|---:|---:|
| Full literal-field projection | 66,072.03 | 62,223.59 | 5.8% | 33,012,555 | 31,612,259 |
| Filtered literal projection, control | 31,907.69 | 31,786.06 | 0.4% | 16,004,010 | 16,003,848 |
| Count of the literal field | 3,845.87 | 2,765.64 | 28.1% | 9,770,232 | 8,369,976 |
| Ordinary preferred-field count | 3,961.14 | 2,742.71 | 30.8% | 10,099,184 | 8,365,880 |
| Ordinary preferred-field projection | 19,721.22 | 18,081.07 | 8.3% | 21,875,216 | 20,141,912 |
| Ordinary indexed projection, control | 65.60 | 64.55 | 1.6% | 35,586 | 35,662 |
| Primary lookup, control | 23.14 | 23.11 | 0.1% | 25,753 | 25,753 |

The literal-field count returns to essentially its pre-step-26 time (2,783.09 µs),
while retaining correct field identity and the covered projection improvements.
Counts allocate 14–17% less in this incremental comparison. Controls are effectively
unchanged. Raw files: `27-preferred-*`.

Four additional tests cover preferred ordinary/escaped fields with repeated keys,
row counts, and scalar array keys; existing multikey and ambiguous-field coverage
also passes. Full .NET 8 and .NET 10 suites: 1,136 passed each, seven existing
skips each; all Release targets build.

## 28. Seek past excluded index keys

Indexed `!=` queries now scan in index order and seek beyond an equal-key run,
using skip-list levels to avoid visiting every excluded entry. The scan retains
its prior cost estimate, document deduplication rules, loop detection, and page
release safety. It also uses the active database collation: the previous binary
comparison returned incorrect results for case/accent-insensitive exclusions.
Those correctness failures are tested separately and are not speedup baselines.

A separate 20,000-row collection has 19,980 zero scores and twenty scores of
minus or plus one. `Score != 0` returns twenty full documents or their row count.
Controls exclude a rare value or a missing value and therefore still traverse
nearly/all 20,000 entries. Comparisons are against step 27; checksums match.

| Workload | Before µs | After µs | Time reduction | Before B/op | After B/op | Allocation reduction |
|---|---:|---:|---:|---:|---:|---:|
| exclusion-rare-results-linq | 3372.07 | 89.89 | 97.3% | 8398024 | 88931 | 98.9% |
| exclusion-rare-results-sql | 3452.81 | 73.66 | 97.9% | 8397480 | 88230 | 98.9% |
| exclusion-rare-results-count | 3433.18 | 33.72 | 99.0% | 8363206 | 51570 | 99.4% |
| exclusion-rare-results-descending | 3490.01 | 91.92 | 97.4% | 8399384 | 85611 | 99.0% |
| exclusion-most-results-control | 3663.69 | 3493.43 | 4.6% | 8363200 | 8373008 | -0.1% |
| exclusion-all-results-control | 3625.20 | 3548.04 | 2.1% | 8363200 | 8363040 | 0.0% |
| exclusion-id-control | 22.57 | 23.24 | -3.0% | 25753 | 25729 | 0.1% |

Complete LINQ retrieval is 37.5× faster and count 101.8× faster in this selective
case, with about 99% fewer allocated bytes. Broad-result controls show small
2–5% time differences and unchanged allocations; no broad speedup is claimed.
The primary lookup difference is consistent with shared-host noise.

Raw `28-exclusion-final-*` files measure the final implementation including its
traversal loop guard. Earlier `28-exclusion-before-*` / `28-exclusion-after-*`
files retain the initial experiment; its missing traversal guard was restored
before final measurement. The smaller initial broad-scan allocation is not a
result of the final change.

Twelve tests cover collation, ascending/descending pagination, repeated and mixed
numeric keys, null/extreme bounds, scalar arrays, multikey deduplication, empty
indexes, index maintenance, primary keys, and forced page release. Full .NET 8:
1,148 passed, seven existing skips; the final guard adjustment passes all twelve
focused tests. All Release targets build.

## 29. Skip duplicate keys at exclusive range starts

Range scans now request an exclusive skip-list seek when their starting bound is
exclusive. Previously an ordinary seek could land anywhere in the boundary's
equal-key run, then walk its remaining entries individually. Inclusive bounds
keep their existing traversal. Both ascending lower bounds and descending upper
bounds use the new seek.

The separate 20,000-row dataset from step 28 is recreated: 19,980 zero scores,
fourteen positive and six negative scores. Complete queries and counts consume
all matching results. This comparison uses step 28 as its baseline; all checksums
match. Raw files: `29-exclusive-*`.

| Workload | Before µs | After µs | Time reduction | Before B/op | After B/op | Allocation reduction |
|---|---:|---:|---:|---:|---:|---:|
| exclusive-greater-linq | 2991.56 | 73.00 | 97.6% | 7500278 | 73858 | 99.0% |
| exclusive-bounded-sql | 3026.87 | 80.84 | 97.3% | 7507312 | 79244 | 98.9% |
| exclusive-greater-count | 3010.02 | 33.09 | 98.9% | 7476110 | 47146 | 99.4% |
| exclusive-less-descending | 137.53 | 50.49 | 63.3% | 271558 | 48242 | 82.2% |
| exclusive-inclusive-control | 3530.84 | 3519.15 | 0.3% | 7721872 | 7721872 | 0.0% |
| exclusive-end-bound-control | 15.33 | 15.83 | -3.2% | 12624 | 12624 | 0.0% |
| exclusive-unique-keys-control | 40.05 | 39.41 | 1.6% | 33577 | 34073 | -1.5% |
| exclusive-id-control | 23.40 | 23.48 | -0.3% | 25729 | 25729 | 0.0% |

The positive range retrieves full LINQ results 41.0× faster and counts 91.0×
faster. The descending negative range improves 2.7×. The old seek can land at
different positions within a duplicate run, so direction and randomized index
layout affect the amount of work avoided. Inclusive scans and other controls
show no convincing latency change. The unique-key range allocates about 0.5 KB
more because an exclusive seek can visit additional skip-list levels.

Twelve additional tests cover inclusive/exclusive endpoints, both orders,
pagination, absent boundaries, collation-equivalent strings, mixed numbers,
nulls, arrays, and sentinel bounds. Indexed string expectations use the database
collation directly: investigation also found an existing binary-comparison bug
in unindexed scalar ranges, which is corrected separately in step 30.
Full .NET 10 suite: 1,160 passed, seven existing skips; all Release targets build.

## 30. Keep scalar range evaluation consistent with index collation

The preceding range tests exposed a pre-existing inconsistency: scalar `>`,
`>=`, `<`, and `<=` evaluated with binary comparison, while indexed scans,
BETWEEN, and ANY/ALL comparisons used the database collation. Scalar evaluation
now receives the current execution collation through the shared expression
factory. This fixes unindexed queries, residual filters, and reused templates;
adding an index no longer changes those case/accent-sensitive results.

This is a correctness correction, not a claimed optimization. The measurements
below use the main 20,000-row collection with same-case ASCII names and numeric
scores, so old and new consumed-result checksums agree. The mixed-case/accent
failures have separate tests and are not benchmark baselines. Raw files:
`30-collation-*`.

| Workload | Before µs | After µs | Time reduction | Before B/op | After B/op | Allocation reduction |
|---|---:|---:|---:|---:|---:|---:|
| collation-string-scan | 34107.54 | 37151.46 | -8.9% | 37407184 | 37407208 | -0.0% |
| collation-numeric-scan | 45809.69 | 45792.15 | 0.0% | 39478205 | 39478234 | -0.0% |
| collation-residual-string-range | 90.86 | 92.52 | -1.8% | 73346 | 73370 | -0.0% |
| collation-indexed-count-control | 1677.09 | 1655.35 | 1.3% | 4354976 | 4354976 | 0.0% |
| collation-id-control | 23.43 | 22.65 | 3.3% | 25729 | 25729 | 0.0% |

The full string scan takes 8.9% longer when performing the configured linguistic
comparison instead of the old binary comparison. Numeric scans and allocations
are unchanged; residual-string timing is within 2%, and indexed controls show
small host variation. The string-scan cost is retained in the report rather than
hidden behind the separate index optimizations.

Seven regression cases fail before the correction. All sixteen new cases pass
after it, covering SQL/LINQ before and after indexing, forced primary-key plans
with residual comparisons, ordinal/case/accent collations, and repeated bindings
with changing execution collations. Full .NET 8: 1,176 passed, seven existing skips.

## 31. Allocate traversal diagnostics only on failure

Index seeks, full scans, and exclusion scans still check their traversal counters
on every visited node. They now construct the diagnostic argument arrays only
when a guard fails. Previously even a successful check allocated a `params`
array, and seeks also populated its key/name arguments. The counter limits,
exception type, and formatted errors are unchanged.

These complete queries use the main 20,000-row collection and compare against
step 30. The primary lookup changes its captured ID on every invocation. All
checksums match; raw files are `31-guard-*`.

| Workload | Before µs | After µs | Time reduction | Before B/op | After B/op | Allocation reduction |
|---|---:|---:|---:|---:|---:|---:|
| guard-primary-count | 2932.60 | 2621.87 | 10.6% | 8353328 | 7713296 | 7.7% |
| guard-scalar-field-count | 2924.78 | 2765.08 | 5.5% | 8365880 | 7725798 | 7.7% |
| guard-exclusion-count | 3788.59 | 3408.05 | 10.0% | 8373896 | 7732880 | 7.7% |
| guard-indexed-range-count | 1710.98 | 1668.86 | 2.5% | 4354976 | 4354136 | 0.0% |
| guard-ordinary-id | 26.74 | 24.54 | 8.2% | 28099 | 27002 | 3.9% |
| guard-ordinary-projection | 68.74 | 67.62 | 1.6% | 35562 | 35082 | 1.3% |
| guard-scan-control | 57808.53 | 57174.29 | 1.1% | 48794014 | 48153997 | 1.3% |

Full counts improve 5–11% and allocate about 640 KB less per 20,000-row traversal.
The ordinary changing-ID lookup improves 8%, with about 1.1 KB fewer allocated
bytes. The filtered range count already bypasses the full-scan loop, so it only
saves its initial seek diagnostics. Projection and scan timing differences are
small; their lower allocation is measurable without a broad latency claim.

Six tests force exhausted traversal budgets in both directions and verify that
seeks, full scans, and exclusions still throw the formatted guard error. Full
.NET 8 and .NET 10 suites: 1,182 passed each, seven existing skips each; all
Release targets build. Reproduction-runner tests and plain/encrypted vector file
compatibility checks also pass through this step.

## 32. Decode extended keys in temporary sort streams

The larger-sort benchmark exposed a pre-existing decoder mismatch. Sort keys
can occupy up to 1,023 bytes, and the writer encodes string/binary lengths across
the type byte and the following length byte. The streaming reader treated the
whole first byte as a BSON type, so keys longer than 255 bytes failed with
`NotImplementedException`, even though their encoded size was valid.

The sort reader now uses the same extended-length decoder as persisted index
pages and consumes the following length byte only for strings/binary values.
The writer and file format are unchanged. This is a correctness fix: the failing
wide-key queries are not counted as speedup baselines. Subsequent merge timings
must include this reader correction in both versions.

Nine new cases fail before the fix; the two 255-byte controls already pass.
The eleven cases cover string and binary lengths across extension boundaries
through 1,021 payload bytes, multibyte UTF-8, ascending full results, descending
pages, and correct reload addresses after each key. Fixtures exercise both single
and multiple default-size sort blocks. Full .NET 8: 1,193 passed, seven existing
skips. All 42 targeted extended-key and vector-planning cases pass on .NET 10; all
Release targets build. Non-length type codes, including vectors, remain intact.

## 33. Merge sorted blocks with a heap

Multi-block sorts now keep the next keys in a heap instead of scanning every
remaining block for each output. Comparisons use the active collation and each
ordering segment's direction. The active block retains priority on tied keys;
other ties retain original block order. The shortcut for identical consecutive
keys, single-block sorting, and bounded top-N sorting are preserved. Exhausted
blocks are excluded from subsequent enumeration, and early disposal releases
all temporary readers and positions through the existing owner.

A separate 20,000-row fixture uses 497-character ASCII titles with a shared
492-character prefix and a permuted unique suffix. These keys create multiple
default 800-KiB temporary sort blocks. Mixed ordering sorts by score ascending
and title descending; the page skips 5,000 rows and returns 2,000. The repeated-key
control sorts the common prefix. Short titles on the main collection fit in one
block; a ten-row limit uses the existing bounded heap. Temporary storage is a
memory stream, so these are not physical-disk throughput measurements.

Both versions include the extended-key reader correction from step 32. Every
query is fully consumed and its checksum includes output order. All checksums
match; raw files are `33-merge-*`.

| Workload | Before µs | After µs | Time reduction | Before B/op | After B/op | Allocation reduction |
|---|---:|---:|---:|---:|---:|---:|
| merge-wide-full | 316384.49 | 269291.86 | 14.9% | 132192568 | 129950864 | 1.7% |
| merge-wide-mixed | 305736.26 | 251600.10 | 17.7% | 144460228 | 142220192 | 1.6% |
| merge-wide-page | 156267.65 | 139148.00 | 11.0% | 69569584 | 68785436 | 1.1% |
| merge-repeated-keys-control | 180630.69 | 183968.02 | -1.8% | 148441480 | 148441304 | 0.0% |
| merge-single-container-control | 113359.39 | 113381.52 | -0.0% | 69109160 | 69109152 | 0.0% |
| merge-topn-control | 38877.46 | 38951.74 | -0.2% | 54310760 | 54310760 | 0.0% |

Full multi-block sorts take 15–18% less time and allocate about 2.2 MB less; the
larger page takes 11% less time. Single-block and top-N controls are unchanged.
The repeated-key control is within 2% with unchanged allocation. Gains depend on
block count, key comparison costs, and how much output is requested.

Eleven tests cover many blocks, both directions, mixed ordering, collation ties,
exact agreement with the previous tie policy, empty/single-block paths, exhausted
blocks, and early disposal. The earlier extended-key and existing sort suites
also pass. Full .NET 8 and .NET 10 suites: 1,204 passed each, seven existing
skips each. All Release targets, 18 reproduction-runner tests, and plain/encrypted
vector file compatibility checks pass through this step.

## 34. Keep loaded index links in one compact owned copy

Loading an index node previously allocated separate forward/backward arrays and
decoded every skip-list pointer immediately. Nodes now keep one owned copy of
the encoded pointer bytes and decode the requested link on access. The copy
remains valid after transaction safepoints release page buffers. Writers update
the page and the owned copy together; the persisted layout is unchanged.
Equality scans, index insertion/deletion, and automatic-ID initialization use the
same link accessor. No lazy access to released page memory is introduced.

The main 20,000-row fixture is compared with step 33. In addition to complete
queries, write controls insert/query/delete a row or update/query/restore one,
leaving the fixture unchanged after each operation. Checksums match for every
case; raw files are `34-links-*`.

| Workload | Before µs | After µs | Time reduction | Before B/op | After B/op | Allocation reduction |
|---|---:|---:|---:|---:|---:|---:|
| links-primary-count | 2685.24 | 2495.18 | 7.1% | 7713296 | 6925712 | 10.2% |
| links-secondary-range-count | 1665.74 | 1597.99 | 4.1% | 4354136 | 3957744 | 9.1% |
| links-exclusion-count | 3503.93 | 3344.41 | 4.6% | 7732880 | 6940488 | 10.2% |
| links-primary-lookup | 25.30 | 23.56 | 6.9% | 27002 | 23923 | 11.4% |
| links-secondary-projection | 68.18 | 65.34 | 4.2% | 35042 | 33377 | 4.7% |
| links-full-scan | 58096.66 | 57968.94 | 0.2% | 48153997 | 47366406 | 1.6% |
| links-insert-query-delete-control | 220.89 | 208.97 | 5.4% | 386611 | 368482 | 4.7% |
| links-update-query-restore-control | 247.32 | 232.71 | 5.9% | 369851 | 349309 | 5.6% |

Measured lookups and counts take 4–7% less time, with 9–11% fewer allocated bytes
in those cases. A full 20,000-row count saves about 0.79 MB; the changing-ID lookup
saves about 3.1 KB. Write/query cycles also improve modestly. Full-scan latency is
unchanged despite its lower allocation; query work outside index traversal still
dominates that case.

Tests verify every pointer at 1, 2, 5, and 32 levels, nonzero buffer offsets,
independent reloaded views, invalid-level writes, and retained reads after page
release. Plain/encrypted file tests cover insertion, updates, deletion, unique and
multikey indexes, reopening, automatic IDs, and small page budgets. Full .NET 8
and .NET 10 suites: 1,211 passed each, seven existing skips each. All Release
targets, 18 reproduction-runner tests, and plain/encrypted file compatibility
checks pass.

## 35. Match scalar root-field indexes independent of field-name casing

BSON document lookup uses ordinal case-insensitive field names, but the planner
previously compared expression text exactly. A query on `score` therefore missed
an index on `Score`, even though both expressions read the same field. The shared
matcher now recognizes this equivalence after proving a canonical scalar root
field. It also applies to bounds, equality ORs, shared OR guards, covered field
projections, ordering, and grouping. Escaped literal field names keep their
identity. Arbitrary computed expressions, string literals, nested paths, and
multikey paths retain exact matching; canonical `Source` and persisted metadata
are unchanged, and stored index expressions need no parsing.

The 20,000-row fixture is compared with step 34. The SQL point workload executes
a complete `SELECT` statement through `LiteDatabase.Execute`; the LINQ point
workload uses an ordinary `BsonDocument` indexer lambda. The other mixed-case
predicates and ordering/grouping use the query builder's expression overloads.
Every query is fully consumed, with matching checksums. Final raw files are
`35-fieldcase-*`. Earlier `35-initial-fieldcase-*` samples remain available; their
SQL-labeled point case used a query-builder predicate instead of a full statement
and is superseded by this table.

| Workload | Before µs | After µs | Time reduction | Before B/op | After B/op | Allocation reduction |
|---|---:|---:|---:|---:|---:|---:|
| fieldcase-point-sql | 25146.63 | 25.60 | 99.9% | 34829040 | 24688 | 99.9% |
| fieldcase-point-linq | 26462.14 | 28.07 | 99.9% | 34827264 | 22988 | 99.9% |
| fieldcase-bounded-range | 26991.48 | 69.55 | 99.7% | 35396704 | 46078 | 99.9% |
| fieldcase-disjunction | 27551.62 | 57.31 | 99.8% | 37074400 | 43150 | 99.9% |
| fieldcase-common-guard | 30898.22 | 142.50 | 99.5% | 39328952 | 88654 | 99.8% |
| fieldcase-covered-order | 85958.54 | 32598.48 | 62.1% | 56832669 | 18978019 | 66.6% |
| fieldcase-group-count | 88716.64 | 30173.09 | 66.0% | 65250467 | 35315056 | 45.9% |
| fieldcase-exact-id-control | 21.91 | 20.10 | 8.2% | 21896 | 21896 | 0.0% |
| fieldcase-exact-projection-control | 67.19 | 66.17 | 1.5% | 33377 | 33514 | -0.4% |

The SQL and LINQ point queries improve by about 982× and 943× respectively because
they stop scanning all 20,000 rows. The bounded range improves by about 388×,
the OR by 481×, and the shared guard by 217×. Covered ordering takes 62% less time
and grouping takes 66% less. These gains require a previously missed index due
to field-name casing; exact-case queries already used their indexes.

Exact-case controls show no clear regression. One before process has a noisy
primary-key batch distribution; the initial comparison had that control within
2%, in the opposite direction. No improvement is attributed to those controls.
Their allocation is unchanged apart from small fixture-dependent index-layout
variation in the projection case.

Sixteen tests compare indexed results with scans, check plans and sort removal,
exercise escaped field names, verify negative computed-literal matching, and
cover updates/deletes and primary-key aliases under ordinal and Turkish
collations. Full .NET 8 and .NET 10 suites: 1,227 passed each, seven existing
skips each. The Release solution builds across all targets.

## 36. Reuse recurring SQL SELECT templates automatically

`LiteDatabase.Execute(string, ...)` now reuses parsed SELECT/EXPLAIN definitions.
The cache is local to the database, uses exact ordinal command text, and holds
at most 128 keys of at most 8,192 characters. The first call retains only its key;
a repeat while resident captures an unbound logical template, and later hits
bind fresh expressions and clause lists to the current parameter document. A
stream of one-off statements therefore does not construct retained templates.
TextReader input, other commands, and oversized statements use the normal parser.

Templates contain no caller parameter values, physical plans, results, or engine
state. Includes, HAVING, grouping, multiple order segments, pagination, SELECT
INTO, FOR UPDATE, EXPLAIN, volatile expressions, system collections, and current
collation still execute normally. Root grouping recognizes the expression's
metadata and keeps its key state separate from caller predicate parameters;
sharing that state could otherwise truncate a streaming residual filter.

The comparison is against step 35 using complete, consumed SQL queries on the
20,000-row fixture. Parameters change on every call. The nested projection sums
both IDs and projected array values; grouped results checksum counts and totals.
The standalone no-FROM case measures SQL expression execution without database
reads and is identified separately. All checksums match. Main raw samples are
`36-sqlcache-*`.

| Workload | Before µs | After µs | Time reduction | Before B/op | After B/op | Allocation reduction |
|---|---:|---:|---:|---:|---:|---:|
| sqlcache-point | 25.34 | 15.59 | 38.5% | 27283 | 23058 | 15.5% |
| sqlcache-combined | 49.75 | 20.58 | 58.6% | 31017 | 22809 | 26.5% |
| sqlcache-projection | 102.13 | 76.63 | 25.0% | 78938 | 69879 | 11.5% |
| sqlcache-bounded-range | 57.94 | 33.10 | 42.9% | 46701 | 38857 | 16.8% |
| sqlcache-nested-projection | 51.72 | 15.70 | 69.6% | 36145 | 20606 | 43.0% |
| sqlcache-grouped-page | 364.84 | 223.21 | 38.8% | 975036 | 955562 | 2.0% |
| sqlcache-count | 73.53 | 31.80 | 56.8% | 66106 | 54824 | 17.1% |
| sqlcache-scalar-no-from | 10.89 | 3.02 | 72.3% | 6633 | 1904 | 71.3% |
| sqlcache-64-statements | 31.91 | 13.71 | 57.0% | 27329 | 19818 | 27.5% |
| sqlcache-256-churn-control | 32.23 | 33.95 | -5.3% | 27378 | 27457 | -0.3% |
| sqlcache-tagged-churn-control | 23.06 | 27.01 | -17.1% | 24819 | 24915 | -0.4% |
| sqlcache-textreader-control | 24.37 | 24.61 | -1.0% | 27283 | 27299 | -0.1% |
| sqlcache-linq-control | 23.40 | 23.91 | -2.2% | 23923 | 23923 | 0.0% |
| sqlcache-scan-control | 38149.53 | 38639.58 | -1.3% | 43622792 | 43618336 | 0.0% |

Repeated database queries take 25–70% less time in these cases: the point lookup
saves 39%, the combined predicate 59%, the bounded range 43%, and the nested
projection 70%. Reusing 64 statements takes 57% less time. Allocation falls by
12–43% for the point/combined/projected cases. The no-FROM expression takes 72%
less time, but that is not a storage-query speedup.

Cold/churn results need separate interpretation. The 256-literal sequence takes
5.3% longer in the main comparison. The tagged control cycles through 256 SQL
comments while keeping the same parsed expression; it takes 17.1% longer in the
mixed suite. A longer isolated comparison with the same 1,024 IDs and ten times
as many iterations measures **24.36 → 24.34 µs** (effectively unchanged), with
**24,819 → 24,915 B/query**. Both results are retained (`36-tagged-*` for the
isolated run). The difference between mixed and isolated runs limits conclusions
about cold latency; cache bookkeeping and its extra allocation remain real costs.
Ordinary LINQ, TextReader, and full-scan controls are within about 2% in the main
comparison. The hot-query gains should not be applied to one-off SQL workloads.

Earlier samples remain available: `36-initial-sqlcache-*` captured templates on
first use; `36-admission-sqlcache-*` added admission but precede the final root-key
binding guard. `36-wide-tagged-*` is a diagnostic isolated run spanning 10,240 IDs;
it is not the fixed-ID comparison quoted above. The final harness keeps the churn
controls' ID distribution fixed when scaling iterations. The first-use admission
change reduced the literal-churn allocation penalty from about 5% to below 1%.

Twenty-seven tests cover fresh-parser parity, changing/nested parameters,
concurrent and interleaved readers, cache admission/eviction and size bounds,
parameter-payload collection, tokenization avoidance, mutable results, volatility,
errors and recovery, index changes, includes/system collections, transaction
rollback, and collation after rebuild. Root grouping is checked with a streaming
filter that reads the caller's `@key`. Full .NET 8 and .NET 10 suites: **1,254
passed each**, seven existing skips each. Release builds across all targets,
18 reproduction-runner tests, and plain/encrypted vector compatibility checks pass.

## 37. Compare LIKE characters without temporary strings

LIKE previously allocated two one-character strings for every character
comparison. It now compares one-code-unit ranges of the existing strings using
the same execution collation. The matcher retains its existing pattern cursor,
backtracking, and NUL-sentinel state. SQL LIKE and ordinary LINQ Contains,
StartsWith, and EndsWith benefit automatically, including residual filters and
index scans that evaluate LIKE. Index-prefix-only checks keep their existing path.

The main fixture has 20,000 rows. Additional 20,000-row fixtures use accented and
decomposed names with linguistic and ordinal collations. Queries consume complete
results, including Boolean projection values; all before/after checksums match.
The final suffix workloads also assert expected IDs independently of the matcher.
The comparison is against step 36. Raw final samples are `37-likechars-*`.

| Workload | Before µs | After µs | Time reduction | Before B/op | After B/op | Allocation reduction |
|---|---:|---:|---:|---:|---:|---:|
| likechars-contains-linq | 39167.70 | 36963.14 | 5.6% | 48254400 | 37415816 | 22.5% |
| likechars-prefix-linq | 57070.14 | 54927.55 | 3.8% | 47366406 | 38572414 | 18.6% |
| likechars-suffix-linq | 38944.42 | 36390.84 | 6.6% | 46429200 | 36105944 | 22.2% |
| likechars-contains-sql | 40902.51 | 38935.42 | 4.8% | 41709464 | 30870824 | 26.0% |
| likechars-projected-sql | 51616.97 | 48039.39 | 6.9% | 48383672 | 37545032 | 22.4% |
| likechars-residual-linq | 95.79 | 97.62 | -1.9% | 81244 | 70738 | 12.9% |
| likechars-id-control | 21.55 | 20.93 | 2.9% | 21896 | 21896 | 0.0% |
| likechars-numeric-scan-control | 45386.06 | 44276.38 | 2.4% | 38050608 | 38050608 | 0.0% |
| likechars-full-index-count | 15437.01 | 12799.20 | 17.1% | 18244856 | 7406272 | 59.4% |
| likechars-prefix-remainder-index | 7787.78 | 6575.31 | 15.6% | 10098288 | 4132608 | 59.1% |
| likechars-prefix-index-control | 309.13 | 307.95 | 0.4% | 236696 | 236788 | -0.0% |
| likechars-unicode-linguistic | 59415.44 | 56672.71 | 4.6% | 42074816 | 33247950 | 21.0% |
| likechars-unicode-ordinal | 41892.43 | 33677.03 | 19.6% | 39861040 | 31274184 | 21.5% |

The full index count and prefix-plus-remainder scan take 16–17% less time and
allocate about 59% fewer bytes. Contains saves roughly **10.8 MB per complete
20,000-row query**: 22.5% fewer allocated bytes with LINQ and 26% fewer with SQL.
Other string-filter scans/projections save about 19–22% in allocation. Their
latency improvement is smaller, generally 4–7% here, while the selective residual
case is effectively unchanged. The non-LIKE controls vary by up to 3%, so small
timing differences should not be overinterpreted.

One before process has a slow ordinal Unicode batch distribution, inflating that
row's main-suite time reduction. A longer isolated before/after comparison,
`37-ordinal-*`, measures **34,614.41 → 33,211.39 µs (4.1% less time)** with the same
21.5% allocation reduction. Use that result for the ordinal latency estimate,
rather than the main table's 19.6%.

`37-legacy-shapes-*` retains the initial diagnostic run. Its shorter suffixes
exercise an existing matcher defect that can return extra rows after the pattern
ends; those variants are excluded from the final performance claims. This step
preserves matcher behavior rather than repairing that separate defect. The final
suffixes are `2345` and `Person1%3456`, with independently checked expected rows.

Twenty tests cover all UTF-16 code units (including isolated surrogates) in seven
collations, fixed and randomized wildcard cases, and complete LINQ/SQL/full-index
queries with changing parameters. The frozen old matcher is a bounded test oracle:
known non-progressing paths do not run indefinitely, and each collation still
checks over 6,300 terminating match cases. Character comparison equivalence is
checked independently across all code units. Full .NET 8 and .NET 10 suites:
**1,274 passed each**, seven existing skips each. All Release targets build.

## 38. Finish LIKE at a terminal wildcard and advance wildcard retries

The matcher now returns immediately when the remaining pattern is only `%`.
Previously it compared every remaining character with a NUL sentinel. Explicit
value/pattern positions also repair existing wildcard defects: matching must
consume the whole value, `_` after `%` still consumes one UTF-16 unit, and literal
NUL is not pattern exhaustion. Retrying a suffix advances its input start, so
matching makes finite progress without recursive calls or scratch allocations.
Literal comparisons retain the active collation and one-code-unit behavior from
step 37. Difficult patterns can still require repeated suffix comparisons; there
is no linear-time complexity claim.

The before assembly is step 37 (`eac3a095`). The same harness runs on both sides
in before/after/after/before order, with nine batches per process and all query
results consumed. Raw samples are `38-likechars-*` and `38-liketail-*`. The short
name workloads reuse step 37's fixture and independently validated suffixes:

| Workload | Before µs | After µs | Time reduction | Before B/op | After B/op | Allocation reduction |
|---|---:|---:|---:|---:|---:|---:|
| likechars-contains-linq | 37695.26 | 36653.15 | 2.8% | 37415760 | 37415760 | 0.0% |
| likechars-prefix-linq | 56950.27 | 53275.38 | 6.5% | 38572330 | 38572330 | 0.0% |
| likechars-suffix-linq | 36640.75 | 36156.56 | 1.3% | 36105888 | 36105888 | 0.0% |
| likechars-contains-sql | 38520.19 | 38107.01 | 1.1% | 30870824 | 30870824 | 0.0% |
| likechars-projected-sql | 48411.22 | 49222.73 | -1.7% | 37545032 | 37545032 | 0.0% |
| likechars-residual-linq | 95.65 | 95.69 | -0.0% | 70682 | 70682 | 0.0% |
| likechars-id-control | 21.33 | 21.65 | -1.5% | 21896 | 21896 | 0.0% |
| likechars-numeric-scan-control | 45955.88 | 45821.30 | 0.3% | 38050608 | 38050608 | 0.0% |
| likechars-full-index-count | 12702.52 | 12095.11 | 4.8% | 7406216 | 7406216 | 0.0% |
| likechars-prefix-remainder-index | 6651.23 | 5917.52 | 11.0% | 4132608 | 4132608 | 0.0% |
| likechars-prefix-index-control | 317.44 | 313.71 | 1.2% | 236696 | 236696 | 0.0% |
| likechars-unicode-linguistic | 56759.13 | 51919.01 | 8.5% | 33247891 | 33247891 | 0.0% |
| likechars-unicode-ordinal | 33991.61 | 33019.07 | 2.9% | 31274128 | 31274128 | 0.0% |

The short-name prefix scan takes 6.5% less time, the full-index count 4.8% less,
and the prefix-plus-remainder index scan 11.0% less. The linguistic Unicode case
takes 8.5% less time. Most other short-string cases change by about 0–3%, with
controls varying by up to 1.5%; small differences are inconclusive. Allocation is
unchanged throughout this comparison.

The additional fixture has 20,000 documents, each with a 270–274-character
name: `Record-<id>-` followed by 256 `x` characters and `-Tail`. It has only the
primary index. These are complete queries that parse records and consume every
returned ID. Prefix/early-contains patterns match all rows; late-match and
nonmatch controls still inspect most characters. Every query checks its expected
ID checksum against the fixed fixture.

| Workload | Before µs | After µs | Time reduction | Before B/op | After B/op | Allocation reduction |
|---|---:|---:|---:|---:|---:|---:|
| liketail-long-prefix-linq | 286788.56 | 51356.70 | 82.1% | 49045272 | 49045216 | 0.0% |
| liketail-long-contains-linq | 289426.24 | 50417.57 | 82.6% | 50485576 | 50485520 | 0.0% |
| liketail-long-prefix-sql | 280856.14 | 42304.48 | 84.9% | 47340608 | 47340608 | 0.0% |
| liketail-all-strings-sql | 284011.16 | 39047.04 | 86.3% | 47340608 | 47340608 | 0.0% |
| liketail-late-match-control | 263371.10 | 249264.20 | 5.4% | 50325576 | 50325520 | 0.0% |
| liketail-nonmatch-control | 251661.53 | 234718.71 | 6.7% | 43823072 | 43823016 | 0.0% |

These early-match queries take **82–86% less time (5.6–7.3× faster)**. For example,
ordinary LINQ StartsWith falls from **286.79 to 51.36 ms**, and SQL prefix matching
from **280.86 to 42.30 ms**. This is a substantial improvement when a short pattern
accepts a long remaining value. Late matches and nonmatches gain a much smaller
5–7%; the shortcut cannot skip their search. Allocations remain effectively
unchanged. These results are specific to the stated warm, in-memory fixtures.

Incorrect legacy results are covered as correctness regressions, not speedup
baselines. Fixed tests include rejecting `Person12344 LIKE '%234'`, underscore
following percent, repeated wildcard retries, empty strings, literal NUL,
surrogates, and literal brackets. An independent dynamic-programming reference
replaces the frozen legacy matcher for all fixed and randomized cases in seven
collations; no cases are skipped for non-progress. The exhaustive character
comparison tests remain. End-to-end SQL and ordinary LINQ suffix queries check
exact expected IDs before and after indexing and with changing parameters.

This step changes existing incorrect wildcard results. It preserves the query
frontends' current translation into LIKE, including their existing treatment of
`%` and `_` in LINQ string-method arguments. Indexed prefix candidate selection
still has a separate comparison path; changing its collation/type handling is a
separate follow-up. These wildcard tests exercise scalar/residual evaluation and
full index matching.

Full .NET 8 and .NET 10 suites: **1,297 passed each**, seven existing skips each.
All Release targets build; 18 reproduction-runner tests and plain/encrypted
vector compatibility checks pass.

## 39. Reuse immutable Boolean predicate results

Comparisons and logical expressions previously allocated a BSON Boolean wrapper
and boxed Boolean for each result. Scalar comparisons, IN/LIKE/BETWEEN, their
ANY/ALL variants, and AND/OR now return one of two internal immutable values.
Nested predicates therefore avoid the same allocation for each visited array
element. Computation, active collation, short circuits, and parameter reads remain
unchanged. Projected documents and arrays stay independent; public BsonValue
constructors and implicit conversions retain their behavior.

The comparison is against step 38 (`f9c95f4c`), using the same production harness
for both versions in before/after/after/before order. There are nine batches per
process, with complete result consumption and matching checksums. Raw samples
are `39-boolvalue-*`. The filter is `boolvalue`; subprefixes select individual cases.

Ordinary filters and projections use the standard 20,000-row fixture. The nested
fixture has 4,000 rows containing 32 integers and 32 string labels each. Boolean
MAP projects all flags with changing range parameters. ANY LIKE finds the final
label, and ALL BETWEEN visits all 32 values. Point lookups, covered counts, and
plain array projections serve as controls. Each measured predicate/projection
query checks its expected fixture checksum.

| Workload | Before µs | After µs | Time reduction | Before B/op | After B/op | Allocation reduction |
|---|---:|---:|---:|---:|---:|---:|
| boolvalue-compound-linq | 49460.68 | 47482.67 | 4.0% | 46084152 | 42164272 | 8.5% |
| boolvalue-compound-sql | 42817.93 | 43157.73 | -0.8% | 45949736 | 42029736 | 8.5% |
| boolvalue-projected-linq | 117416.43 | 116350.33 | 0.9% | 73514618 | 66234470 | 9.9% |
| boolvalue-projected-sql | 59789.08 | 56123.43 | 6.1% | 57863560 | 51143560 | 11.6% |
| boolvalue-residual-linq | 121.60 | 115.87 | 4.7% | 82550 | 76150 | 7.8% |
| boolvalue-id-control | 25.95 | 24.62 | 5.1% | 25555 | 25555 | 0.0% |
| boolvalue-covered-count-control | 40.81 | 39.63 | 2.9% | 55961 | 55961 | 0.0% |
| boolvalue-nested-map-linq | 70910.35 | 67663.37 | 4.6% | 47327187 | 26942701 | 43.1% |
| boolvalue-nested-map-sql | 48372.87 | 45283.66 | 6.4% | 44504808 | 24120808 | 45.8% |
| boolvalue-nested-any-like | 59854.75 | 58321.46 | 2.6% | 30777544 | 23385544 | 24.0% |
| boolvalue-nested-all-between | 35699.65 | 34873.10 | 2.3% | 29273544 | 21881544 | 25.3% |
| boolvalue-nested-array-control | 41751.29 | 42206.12 | -1.1% | 23644856 | 23644856 | 0.0% |

The strongest improvement is allocation: nested Boolean projections save roughly
**20.4 MB per complete query**, reducing allocation by **43.1% with ordinary LINQ**
and **45.8% with SQL**. Nested ANY LIKE and ALL BETWEEN each save **7.4 MB**, or
**24–25%**. Compound filters save about **3.9 MB (8.5%)**; Boolean projections save
**6.7–7.3 MB (10–12%)**. The selective residual query saves **6.4 KB (7.8%)**.
Control allocations are unchanged. Measured Gen0 collections for nested Boolean
projections fall from about 5.33 to 3.00 per LINQ query and 5.00 to 2.67 per SQL
query under this harness.

Timing improvements are modest and less conclusive. The pooled measurements show
4.6–6.4% less time for nested Boolean projections and 6.1% less for the SQL Boolean
projection. However, the unchanged point-lookup control also improves by 5.1%,
and the covered-count control by 2.9%, while the plain-array control is 1.1% slower.
Several affected workloads are within this variation. Treat this step as a
substantial reduction in allocation for predicate-heavy arrays, with limited
evidence for a general latency improvement on this shared host. These incremental
percentages must not be multiplied by earlier steps.

Twenty tests cover all nine empty ANY/ALL predicate families, required type errors
and short circuits, changing bound parameters and active collation, BSON/JSON
round trips, concurrent nested projections, ordinary LINQ/SQL Boolean arrays,
and independent persisted Boolean fields during index updates. Tests verify
results and mutable-container independence rather than singleton identity.
Full .NET 8 and .NET 10 suites: **1,317 passed each**, seven existing skips each.
All Release targets build; 18 reproduction-runner tests and plain/encrypted
vector compatibility checks pass.

## 40. Reuse parsed text expressions with fresh bindings

Repeated public `BsonExpression.Create(string, ...)` calls now reuse logical
templates in a process-wide cache. This benefits ordinary string Where/Select
calls and `FindById`, which builds a text predicate internally. The cache admits
up to 128 exact texts of at most 8,192 characters. First use records only the key;
a repeat captures an unbound template, and subsequent hits copy nodes and field
sets while binding the current caller's parameters. Parsing, binding, and
execution run outside the cache lock.

Canonical Source, scalar/ANY metadata, volatility, explicitly null bindings, and
current collation are preserved. No caller parameters, results, or physical plans
are retained. Admission follows successful parsing and EOF validation, and the
Tokenizer parser entry points still consume their input. Tests can bypass both
compiled and parsed reuse for independent comparisons.

These measurements compare step 39 (`68b3ebd1`) with this step using production
assemblies and the same harness. The single-threaded run uses the standard
20,000-row fixture, CPU 2, and before/after/after/before order with nine batches
per process. Every query consumes its results; all paired checksums match. Raw
samples are `40-textir-*`; run the `textir` filter to reproduce this table.

| Workload | Before µs | After µs | Time reduction | Before B/op | After B/op | Allocation reduction |
|---|---:|---:|---:|---:|---:|---:|
| textir-find-by-id | 23.82 | 15.53 | 34.8% | 23149 | 20748 | 10.4% |
| textir-point-named | 23.27 | 15.69 | 32.6% | 23125 | 20716 | 10.4% |
| textir-point-positional | 24.11 | 15.46 | 35.9% | 23149 | 20748 | 10.4% |
| textir-combined | 61.56 | 37.61 | 38.9% | 38276 | 32825 | 14.2% |
| textir-bounded-range | 70.04 | 44.55 | 36.4% | 46453 | 41025 | 11.7% |
| textir-projection | 118.37 | 87.74 | 25.9% | 93131 | 81159 | 12.9% |
| textir-nested-projection | 54.76 | 16.54 | 69.8% | 35889 | 21270 | 40.7% |
| textir-count | 54.59 | 33.26 | 39.1% | 60480 | 55048 | 9.0% |
| textir-64-shapes | 23.55 | 16.04 | 31.9% | 23147 | 20738 | 10.4% |
| textir-256-churn-control | 23.21 | 24.48 | -5.5% | 23147 | 23147 | 0.0% |
| textir-literal-churn-control | 46.02 | 47.56 | -3.4% | 23410 | 23501 | -0.4% |
| textir-long-text-control | 54.63 | 54.72 | -0.2% | 23111 | 23031 | 0.3% |
| textir-prebound-control | 15.65 | 15.27 | 2.4% | 20716 | 20716 | 0.0% |
| textir-linq-control | 23.36 | 21.55 | 7.7% | 21437 | 21437 | 0.0% |
| textir-sql-control | 18.18 | 13.36 | 26.5% | 19780 | 19780 | 0.0% |
| textir-scan-control | 50405.93 | 50680.42 | -0.5% | 36013699 | 36011144 | 0.0% |

Repeated text-based database queries take **26–70% less time** in the main suite.
`FindById` drops from **23.82 to 15.53 µs (34.8%)**. Named/positional point
predicates improve by 33–36%, the combined predicate by 39%, the bounded range by
36%, and the nested projection by 70%. Allocation falls by about 10% for point
lookups and 41% for the nested projection. Reusing 64 expression texts takes 32%
less time. The explicit prebound control is already near the resulting point
lookup time; callers of these ordinary methods gain reuse automatically.

Churn remains a tradeoff. The 256-comment working set deliberately exceeds cache
capacity while keeping the compiled expression shape fixed; it is 5.5% slower in
the main run with unchanged measured total allocation. The 256-literal workload
is 3.4% slower and adds about 91 B/query. Oversized text and full-scan controls are
effectively unchanged. The benefit requires enough reuse for templates to stay
resident and should not be applied to one-off expressions.

One SQL control process measures about 21.7 µs instead of the other before
process's 14.1 µs, inflating its pooled apparent gain to 26.5%. This control has
unchanged allocation and is not a claimed SQL optimization. Ordinary LINQ's
control also varies. Longer isolated control measurements follow below; the main
samples remain available rather than being discarded.

The isolated runs use ten times as many iterations with the same fixed ID
sequence, still in before/after/after/before order. Raw samples are
`40-isolated-textir-*-control-*`; reproduce with the corresponding workload
prefix and final scale argument `10`.

| Workload | Before µs | After µs | Time reduction | Before B/op | After B/op | Allocation reduction |
|---|---:|---:|---:|---:|---:|---:|
| textir-sql-control | 13.21 | 13.47 | -1.9% | 19801 | 19801 | 0.0% |
| textir-linq-control | 22.40 | 22.77 | -1.7% | 21457 | 21457 | 0.0% |
| textir-256-churn-control | 22.83 | 23.91 | -4.7% | 23067 | 23147 | -0.3% |

SQL and ordinary LINQ controls are within 2% in isolation, with unchanged
allocation. Use these as the control estimates rather than the main suite's
larger apparent gains. The churn penalty persists: 4.7% longer and 80 B/query
more in isolation, consistent with the main run's roughly 5% latency cost.

The parallel comparison uses eight workers, each with its own 20,000-row database,
on CPUs 2,4,6,8. Every batch completes 1,024 point queries per worker. This exercises
the process-wide cache across independent engines. The time column is wall time
divided by all completed queries (a throughput measure), **not individual request
latency**. Allocation uses process-wide `GC.GetTotalAllocatedBytes`, including
worker scheduling. Each batch asserts the expected ID sum. Run `textir-parallel`;
raw samples are `40-textir-parallel-*`.

| Workload | Before µs | After µs | Time reduction | Before B/op | After B/op | Allocation reduction |
|---|---:|---:|---:|---:|---:|---:|
| textir-parallel-find-by-id | 8.65 | 6.18 | 28.5% | 21913 | 19592 | 10.6% |
| textir-parallel-combined | 14.11 | 6.90 | 51.1% | 26753 | 21488 | 19.7% |
| textir-parallel-prebound-control | 6.20 | 6.01 | 3.0% | 19560 | 19560 | 0.0% |

Amortized time falls by 28.5% for FindById and 51.1% for combined predicates,
equivalent to approximately **40% and 104% higher throughput** in this fixture.
The prebound control changes by 3.0%. Allocations decrease by 11% and 20% for the
two affected workloads. This run checks an eight-worker workload across four
cores; it is not a claim about scaling to arbitrary worker or database counts.

Twenty-seven tests cover admission and eviction, bounded text length, concurrent
publication and bindings, collection of original parameter payloads and nodes,
fresh-parser metadata/execution parity, mutable field sets and results,
volatility, null parameters, parser and execution errors, tokenizer consumption,
multikey predicates, live index changes/rebuilt collation, and computed-index
maintenance. Full .NET 8 and .NET 10 suites: **1,344 passed each**, seven existing
skips each. All Release targets build; 18 reproduction-runner tests and
plain/encrypted vector compatibility checks pass.

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
- Full `LiteDB.Tests` with `tests.runsettings`: 1,344 passed on .NET 8 at step 40;
  1,344 passed on .NET 10 at step 40; focused sort and query suites also pass on .NET 8. Each full
  run has seven existing skips.
- Reproduction-runner tests: 18 passed.
- Vector file compatibility: ordinary v8 round trips and promoted vector-file
  rejection by LiteDB 5.0.21 pass for plain and encrypted files.
- C# size checks and whitespace checks pass; new C# files remain under 300 lines.
- Each benchmark comparison checks matching consumed-result checksums.
