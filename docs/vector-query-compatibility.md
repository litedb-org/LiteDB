# Vector query semantics and file compatibility

Fixes [upstream issue #2881](https://github.com/litedb-org/LiteDB/issues/2881).

## Query planning

Ordinary predicates, projections, grouping, and ordering consider only ordinary
skip-list indexes (`IndexType == 0`). Vector indexes have their own access path.
`VECTOR_SIM(field, target)` and `field VECTOR_SIM target` both evaluate cosine
distance `1 - dot(a, b) / (length(a) * length(b))` through the ordinary SQL pipeline.
Scalar cosine predicates stay separate from
API metric thresholds even when combined with an explicit vector query. API
arguments select the vector index and target; scalar predicates remain residual
filters and prevent premature ANN candidate truncation. Explicit `WhereNear` and `TopKNear` calls
continue to use the selected vector index's metric (including minimum similarity
for dot product).

SQL vector expressions use the ordinary query planner, including bounded ordering
queries. Ordinary WHERE indexes remain eligible; a vector index does not override
them. SQL never uses HNSW, including `ORDER BY VECTOR_SIM(...) LIMIT k`, and SQL
`EXPLAIN` never reports `VECTOR INDEX SEARCH`. This is a behavior change: ANN is
available only through the explicit `TopKNear` and `WhereNear` APIs. Sorting uses
the existing temporary-file sorter.
They preserve documents with missing, invalid, zero-length, or dimension-mismatched
vectors and use the scalar expression's null distance and normal null ordering.
SQL predicates remain in the normal pipeline so null comparison rules are
identical with and without an index. Adding an index does not remove these rows.

Explicit `WhereNear` / `TopKNear` queries may use approximate HNSW top-k search
when they have a finite limit, no offset, residual filters, includes, or grouping,
and their complete ordering is provided by that search. This remains approximate:
the graph's candidate search does not guarantee exact nearest-neighbor recall.
With a matching vector index, these APIs search valid vector hits and do not
synthesize undefined neighbors. Without a matching index, they use ordinary scalar
cosine evaluation, including null distances and normal null ordering. That fallback
can return documents with undefined vectors.

Indexed vector queries with residual operations, offsets, or no finite limit
evaluate all valid vectors through the collection's primary index. They do not
inherit HNSW's default 32-candidate ceiling. Queries apply filters, grouping,
requested sorting, offset, and limit in the normal pipeline. Sorting is removed
only when bounded ANN supplies the complete requested ordering.
Descending order, secondary keys, and an independent vector target retain their
requested sort. Explicit vector APIs reuse the index metric score for matching
primary keys, including Euclidean and dot-product ordering with secondary keys.
`WithScore` preserves those metric scores through complete evaluation, includes,
projection, and sorting. Scored query snapshots retain the same API predicate
identity as their copied WHERE terms, so scoring preserves both metric thresholds
and eligibility for bounded ANN. Computed index expressions retain their canonical
expression source, including expressions such as `COALESCE($.Embedding, [0,1])`.
Expression matching preserves string-literal case while allowing field-name case
differences, so computed expressions selecting different vectors cannot share an
index or replace each other's ordering.
For hand-built `Query` objects, an unparseable `VectorField` selects no vector
index; ordinary predicates and ordering still execute. Public vector API overloads
continue to validate their expressions when the query is constructed.

A query supports at most one `WhereNear` predicate. Combining it with `TopKNear`
requires the same expression and target, in either call order. A repeated
`WhereNear` or a mismatched combination throws `InvalidOperationException` before
changing the query. This avoids silently evaluating an earlier API threshold as
scalar cosine. Matching `TopKNear` calls retain the threshold; an ordinary scalar
`Where` predicate remains a separate cosine condition.

Exact vector evaluation streams projected documents, retains only the current
row's score, and sorts keys and addresses through the existing temporary-file
sorter. Sorted documents are reloaded by address. It does not retain an entire
collection of documents in memory. A selective projection still reads the vector
field needed for scoring. Exact scans require O(N) distance work and sorting can
require O(N log N) work and temporary disk space. Simple bounded top-k queries
retain the existing ANN performance characteristics. Equal-distance neighbors
have no specified relative order unless the query supplies a tie-breaker.

## Ordinary v8 compatibility and vector format 9

New databases now use **format v10**, which protects both vector pages and the
persisted comparison changes described in [index compatibility](collation-runtime-compatibility.md).
Writable opens of v8/v9 files migrate indexes automatically; read-only opens that
need migration request a writable open first. Simple member-path vector indexes
retain their graph and metadata. Computed vector indexes are regenerated because
comparison changes can affect their expressions. Existing v7 `Upgrade=true`
rebuild and read-only upgrade semantics remain available.

The v10 boundary is durably written before migrated pages enter WAL and cannot
be lowered by rollback, replay, or checkpoint. Encryption and caller-stream
wrappers forward durable flushes. Released v8/v9 readers reject v10, including
ordinary databases. Unknown versions report `UNSUPPORTED_FILE_VERSION`. Do not
change the version byte to bypass this protection.

## Validation

`Issue2881_VectorPlanning_Tests` compares indexed results with a full-scan cosine
reference, including more than 32 documents, filtering, offsets, strict bounds,
grouping, descending ordering, and distance ties.
`Issue2881_UndefinedVector_Tests` covers missing/invalid/zero/non-finite vectors,
null ordering, bounded and composite sorts, SQL predicates, and grouping. The initial test-only commit
also corrects an existing test that asserted the broken removal of secondary sort.
Its focused run had 22 failures and 9 passing regressions before the fix.

`Issue2881_VectorExecution_Tests` covers ordinary filter-index selection, metric
ordering with includes, parser arity, and projected exact queries spilling sort
keys to temporary storage. Its initial execution tests had four failures and one
passing regression before the query follow-up fix.

`Issue2881_VectorFormat_Tests`, `Issue2881_VectorPromotion_Tests`, and
`Issue2881_VectorPromotionFailure_Tests` cover version gating, unchanged ordinary
files, the current format through commit/rollback, older WAL headers, encryption, shared
connections, concurrent writes, failed writes/flushes, and vector rebuild. These tests now include the v10 index-migration boundary. Existing
recovery fixtures run with their original version headers.

Run `python3 scripts/test-vector-compatibility.py` for the cross-version check.
It uses separate processes for the current library and NuGet LiteDB **5.0.21**.
The current engine migrates ordinary legacy files and retains their contents.
The old engine then refuses read/write/rebuild/upgrade without changing the data
file. Current files with vectors or empty vector indexes remain readable by the
current engine. This runs for plain and encrypted files, along with vector rebuild
checks, in Linux CI. Run `scripts/test-index-compatibility.py` for the persisted
ordering and unique-collision migration matrix.

`Issue2881_VectorPredicate_Tests` distinguishes scalar cosine predicates from API
thresholds, index selection, targets, and candidate limits. Together with
`Issue2881_DurablePromotion_Tests` and `Issue2881_ReadOnlyUpgrade_Tests`, the review
reproduction commit had 12 failures and 3 passing regressions before the fixes.
Durability tests track the underlying file's durable flush and inject failures
there for plain/encrypted promotion; v7 upgrade tests use existing fixtures and
verify backups and read-only access. Header savepoint coverage verifies that
restoring an older buffer retains the promoted version.

`Issue2881_VectorComposition_Tests` and the bounded scored-query sort-spill test
cover Euclidean/dot-product parity, projection, snapshot reuse,
computed expressions, ANN eligibility, and rejected API combinations. The separate
review reproduction commit has 18 failing cases before the fixes.

`Issue2881_VectorAggregate_Tests` covers repeated aggregates over included documents
for threshold and top-k queries, with and without included-field filters. Its
separate test-only commit reproduces four failures. Aggregate replay reapplies
includes after reloading each document by address, preserving the bounded document
memory usage of exact vector execution.
The aggregate replay fix also applies to ordinary queries. `AggregateIncludeReplay_Tests`
covers an ordinary secondary index with repeated `FIRST` and `SUM` expressions,
including filters on referenced fields and sorted/unsorted execution. No vector
data or index is involved in that fixture.

`Issue2881_VectorExpressionIdentity_Tests` covers literal-sensitive index selection,
API composition, scalar ordering, and field-name casing. Its test-only commit
reproduces eight failures and retains one passing field-name control.
`Issue2881_QuotedVectorField_Tests` adds eight regressions for quoted member names,
including computed expressions and both API composition orders. Quoted path
identifiers retain case-insensitive matching; quoted values remain case-sensitive.

## Durable flush cost

Durable flush requests now reach the underlying file through the encryption and
caller-stream wrappers. This changes promotion, checkpoint, recovery-marker
writes, and stream initialization where that helper is used. Checkpoints on
previously soft-flushed encrypted/custom file streams now include a real disk sync,
so their latency depends on the storage device. WAL commit still uses parameterless
`Flush()` in `WriteLogDisk`; the change does not add fsync to every commit.
`DiskService`'s other header flush is `MarkAsInvalidState`, not the WAL commit path.

Run `dotnet run --project tools/VectorFlushProbe -c Release -p:TestingEnabled=false`
for a small production-build probe using real data and WAL files through caller
streams. It warms the stream pools, disables automatic checkpoints, performs
1,000 separate ordinary transactions, checkpoints, and promotes before vector
commit. A local Linux/.NET 8 sample produced:

| Mode | Phase | Time (ms) | Data durable flushes | WAL durable flushes |
| --- | --- | ---: | ---: | ---: |
| Plain | 1,000 commits | 99.788 | 0 | 0 |
| Encrypted | 1,000 commits | 110.359 | 0 | 0 |
| Plain | Checkpoint | 24.230 | 1 | 0 |
| Encrypted | Checkpoint | 34.024 | 1 | 0 |
| Plain | Promotion before commit | 5.960 | 1 | 0 |
| Encrypted | Promotion before commit | 5.163 | 1 | 0 |

These are single local timing samples; filesystem caching, encryption, and storage
latency affect them. The flush counts establish the operation boundaries; a
production throughput comparison needs repeated runs on representative storage.
