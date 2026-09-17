# Shared logical expressions for LINQ and SQL

Typed LINQ expressions now construct `BsonExpression` nodes directly through
`LinqExpressionTranslator` and `BsonExpressionFactory`. Type resolvers return
structured bindings for methods, members, constructors, collection operations,
and nested lambdas. Translation neither tokenizes resolver templates nor parses
generated expression text. Unsupported constructs still report the original
expression and the unsupported member or node.

The SQL/expression parser uses the same factories. Both frontends produce the
existing logical `Query`/`BsonExpression` representation, including cardinality,
field dependencies, volatility, parameter references, and predicate operands.
`QueryOptimization` continues to choose a physical `QueryPlan` using the live
collection snapshot. No separate executor, database format, compiler preview
feature, or source generator is required.

Factories format `Source` as an output of construction. Shared formatting keeps
existing canonical expression syntax, diagnostics, and persisted expression
indexes compatible. The `ANY =` to `IN` optimizer rewrite also composes nodes
directly. SQL, string predicates, and persisted index definitions retain their
textual parser entry points.

## Reusing a compiled expression

`BsonExpression.Bind(BsonDocument)` creates an independently bound expression
without translation, parsing, or compilation. It preserves the logical shape and
copies the predicate nodes and field sets, while sharing compiled delegates. The
input parameter document belongs to the caller and must remain unchanged while
that binding executes. Distinct documents can be bound and executed concurrently.
The original expression's parameters are unaffected.

```csharp
var minimum = 18;
var city = "Linz";
var template = mapper.GetExpression<Person, bool>(
    p => p.Age >= minimum && p.City == city);

// Use the names in template.Parameters; this translation creates p0 and p1.
var predicate = template.Bind(new BsonDocument
{
    ["p0"] = 21,
    ["p1"] = "Vienna"
});
var results = people.Query().Where(predicate).ToList();
```

LINQ parameters retain their current `p0`, `p1`, ... naming and mapper
serialization behavior. SQL templates use their supplied parameter names.
Nested MAP/FILTER/SORT and array-index expressions receive the current execution's
parameters explicitly, so cached delegates cannot reuse another binding's values.
Embedded nested templates hold no parameter documents, preventing retained first-use
parameter payloads in compiled delegates and automatically cached LINQ shapes.
Collation is likewise supplied at execution time. There is no global translation
cache keyed only by CLR type; each translation observes its own mapper. Reuse a
template with the mapping under which it was translated. Rebinding stores no
index choice; creating or dropping an index still changes subsequent planning.

## Automatic LINQ reuse

Ordinary `Where`, `Select`, and other mapper-translated LINQ calls now reuse
logical templates automatically. Each mapper owns a bounded 256-entry shape
cache, arranged as 64 buckets of up to four entries. Bucket selection mixes all
hash bits to reduce patterned collisions; atomic publication keeps reads lock-free
and avoids duplicate concurrent insertions. Its keys contain expression structure, CLR types, members, methods, and
lambda parameter identity, excluding captured objects and constant values.
Hash collisions are checked against the full structural key.

Each call reevaluates and serializes its parameter slots in the original order,
including repeated getter accesses. The cache checks the mapper's enum setting
and the mapped members used during translation, including changes made through
the publicly mutable entity/member metadata. Bindings receive independent
parameter documents and field sets. A small per-thread scratch visitor is reused
and cleared in `finally`; reentrant translation rents another visitor.

Captured helper calls reuse a closure-free CLR evaluator. Every constant is read
from its occurrence in the current expression tree. Compilation is deferred until
the shape is reused; ordinary captured fields and properties keep their reflection
path. Evaluation order, exception wrapping, and live mapper serialization remain
unchanged. Nested member/list initializers and ambiguous reused binding nodes use
the direct translator.

Nested MAP/FILTER/SORT and bracket filters reuse their root source across elements,
or use an empty source when unused. Scalar MAP selectors execute without creating
a per-element wrapper enumerator; enumerable selectors still flatten their values.

Indexers whose evaluated arguments become part of canonical `Source`, synthetic
enum/DbRef bindings, invoked lambdas, unsupported shapes, and shapes over 512
tokens use the direct translator. They remain functional and still use the
compiled-delegate cache. No physical index choice is cached. Explicit `Bind`
remains available to callers that want to avoid even the structural cache lookup.

See [`query-optimization-benchmarks.md`](query-optimization-benchmarks.md) for
separately measured automatic reuse and shared query optimizer improvements.

## Shared predicate optimization

The engine inspects the same expression nodes for LINQ and SQL. Equality ORs on
one scalar indexed expression become ordered IN seeks. OR branches with equivalent
leading scalar equalities can use that shared indexed guard while retaining the
entire OR as a residual filter. Guard extraction is bounded and skips includes;
it does not move later conditions ahead of throwing or volatile expressions. Separate scalar index
bounds and IN/BETWEEN constraints are intersected before index selection, with
only the scan-enforced filters removed. Contradictory scalar path constraints
produce an empty pipeline input, preserving aggregate behavior. Constant Boolean
guards expose indexable predicates while respecting short circuits and volatility.
Boolean identity comparisons around predicates are removed so composed LINQ
Contains calls reach the same normalization and index selection. ANY is explicit
node metadata, preserved through grouping, rewrites, and binding. Normalization
constructs nodes directly and preserves the original reusable tree.

Values, collation, and available indexes are read for each execution. Internal
volatility metadata distinguishes changing parameters from functions such as
RANDOM/NOW, including calls nested in MAP or array expressions; only values
that remain stable during the execution can be intersected. Multikey
ANY/ALL bounds are not intersected: different array elements can satisfy them.
The existing pipeline already filters before sorting and projection, and defers
includes that are not needed by filters; these changes do not reorder those stages.

## Index-only row aggregates

After index selection, the planner recognizes pure COUNT/ANY projections over
source rows or scalar member paths. If the selected index enforces every filter
and no remaining grouping, sort, include, vector operation, or update lookup is
needed, a dedicated pipe counts the index's deduplicated document stream. It
preserves offset/limit and transaction safepoints and stops early for ANY-only
projections. Multiple COUNT/ANY fields share one traversal. Missing/null scalar
member paths still emit one value per row, matching existing aggregate semantics.

Recognition inspects the structured expression tree, including SQL aliases and
the expressions used by ordinary Count/LongCount/Exists. Those helpers now build
their fixed logical templates once through the shared factories. Each invocation
binds its own parameter document, including GROUP BY key mutations, and restores
the caller's projection even after an error. EXPLAIN reports
`indexAggregatePipe` and a `none` lookup loader. Other aggregate shapes use the
existing document pipeline; no count, index choice, or parameter values are cached.

## Persisted index expressions

Collection snapshots retain canonical index text for planning. They construct a
persisted index's expression only when an operation evaluates it, then reuse that
expression on the metadata instance. Ordinary reads need no index-expression
parsing; writes and vector evaluation request it when needed. New index definitions
still validate eagerly, and subsequent snapshots observe live index metadata.

Scalar IR metadata also identifies scans with one index entry per document.
Those scans, primary indexes, unique indexes, and canonical preferred root-field
indexes avoid redundant address sets. Multikey scans retain document deduplication.
Preferred and covered field matching use the same escaping as expression factories,
so literal field names cannot be confused with nested, multikey, or computed paths.

Indexed not-equal predicates scan in index order and use an exclusive skip-list
seek to jump past equal keys. Comparison uses the database collation, and multikey
results retain one output per document. Their cost estimate remains unchanged.
Exclusive range starts use the same seek to skip duplicate boundary keys; inclusive
range endpoints retain their existing traversal. Scalar range evaluation also uses
the execution collation, keeping unindexed and residual comparisons consistent
with indexed ranges and ANY/ALL predicates. Traversal loop guards retain their
checks while creating diagnostic argument arrays only on failure.

## Limited sorting

For residual ORDER BY with a positive limit and `offset + limit <= 1024`, a
bounded maximum heap retains only the best requested keys and reload addresses.
Comparison uses the active collation, each segment's direction, and input order
to break ties. Every input key is still evaluated and checked against the sort-key
size limit. Larger or unbounded requests retain the disk-capable sort path.
The temporary-stream reader decodes extended string/binary key lengths using the
same header rules as index pages, including UTF-8 payloads above 255 bytes.
Includes and aggregate replay continue to use the normal lookup pipeline, and
small exact vector rankings can use the same bounded sorter.

## Verification

The existing LINQ expression corpus now checks canonical text, type, cardinality,
fields, source usage, immutability, and predicate operands against its parsed
expectations. A test-only thread-local guard rejects tokenizer construction while
translating; compiled-cache bypass prevents a parsed delegate from masking a
broken direct expression. Additional tests compare uncached execution over
randomized documents and predicates, SQL/LINQ results and full EXPLAIN documents,
expression indexes, custom mapping/collations, and concurrent bindings.

The benchmark harness is in `tools/QueryIrBenchmarks`. It measures allocations
and complete queries, and can reference an unmodified baseline assembly. See
`query-ir-benchmarks.md` for measured results and reproduction details.

## Extension boundary

The internal factories are the construction boundary for a future opt-in source
generator. Generated code can build a logical template and bind parameter values;
it must still enter `QueryOptimization` to select a plan at execution time.
A supported public generated-template surface and specialized materializers remain
separate follow-ups. This change preserves canonical `Source` identity for
index matching and the bounded compiled-delegate cache. It introduces no physical
plan cache or experimental call-site interceptors.
