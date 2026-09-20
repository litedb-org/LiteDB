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
LINQ MAP/FILTER nodes also retain the selector's immutability and source-dependency
flags. Parameters and volatile calls inside a selector therefore prevent immutable
classification. Explicit SQL MAP/FILTER keeps its historical input-only handling
of immutability and source usage; both frontends propagate nested volatility.
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

See [`query-optimization-benchmarks.md`](https://github.com/litedb-org/LiteDB-Artifacts/blob/main/pull-requests/2905-shared-query-ir/query-optimization-benchmarks.md) for
separately measured automatic reuse and shared query optimizer improvements.

## Automatic SQL reuse

Repeated `LiteDatabase.Execute(string, ...)` SELECT and EXPLAIN statements reuse
parsed logical templates automatically. A database-local least-recently-used
cache holds at most 128 statement keys, each at most 8,192 characters. The first
execution retains only its key; a repeat within that window captures an unbound
template, and later hits bind new expressions and clause lists to the current
parameter document. This admission rule avoids constructing templates for a
stream of one-off statements. Keys use exact ordinal SQL text, preserving string
literals and grammar; no parameter values enter the key or retained template.

Includes, grouping, HAVING, multiple order segments, pagination, SELECT INTO,
FOR UPDATE, and EXPLAIN retain their ordinary execution paths. SELECT without
FROM reevaluates expressions with the current engine collation, including volatile
functions. System collections create fresh inputs on each call. Bound `$`
projections keep root-grouping behavior with isolated key state, preserving caller
parameters used by residual filters. Creating/dropping indexes, changing data, or rebuilding collation still
changes subsequent executions because physical plans and results are never cached.

The TextReader overload, other SQL commands, and longer statements keep their
parser paths. Parsing failures do not populate the cache. The first two calls
still parse; benefits apply to statements reused enough to remain resident.
See step 36 in [`query-optimization-benchmarks.md`](https://github.com/litedb-org/LiteDB-Artifacts/blob/main/pull-requests/2905-shared-query-ir/query-optimization-benchmarks.md) for complete queries, allocation,
and churn controls.

## Automatic text-expression reuse

Repeated `BsonExpression.Create(string, ...)` calls reuse parsed logical templates
through a process-wide cache of at most 128 exact expression texts, each at most
8,192 characters. This applies automatically to string predicates/projections and
to helpers such as `FindById` that construct text expressions internally. First
use keeps only the key; a repeat captures an unbound template, and later hits copy
its nodes and field sets with the current caller's parameter document.

The cache holds neither caller parameters nor physical plans or results. Binding
preserves canonical Source, scalar/ANY metadata, volatility, and current collation.
Explicitly null parameter documents retain their previous execution behavior.
Malformed or oversized input follows the parser path; admission occurs only after
successful parsing and the end-of-input check. Tokenizer-based parser entry points
still consume their streams, and test scopes that disable compilation reuse also
bypass this cache. The cache lock protects lookup/publication only; parsing,
binding, and execution happen outside it. See step 40 of the benchmark report for
hot queries, churn, and concurrent throughput measurements.

## Shared predicate optimization

ORs of scalar member-path bounds can use an ordered union of index ranges. Each
arm may intersect `<`, `<=`, `>`, `>=`, equality, `IN`, and `BETWEEN` constraints
on the same key. Ordinary LINQ `Contains` participates inside ORs as well.
The planner reads current parameters, including arithmetic bounds, discards empty
arms, and merges overlapping or connected ranges using the active collation.
Open endpoints remain open when neither arm covers the boundary. The resulting
disjoint scans support ascending/descending order, pagination, covered projections,
and row aggregates without duplicate documents or residual OR evaluation. Set
intersections discard keys outside the final bounds before constructing ordered
sets, and the union merges matching keys into the ranges that already cover them.
Parameter lists can exceed the structural node budget. An existing indexed scalar
equality avoids expanding a list into a more expensive candidate.

Equality and flat range OR analysis validate the complete shape before allocating
key or interval buffers. A second traversal reads accepted values in their original
left-to-right order, using each leaf's own bindings. Rejected flat shapes can fall
back to nested Boolean analysis without temporary branch lists. The presence of
a cheaper scalar equality is memoized only within the current optimizer instance,
whose normalized terms and index snapshot are fixed. Candidate costs and selection
remain unchanged: an empty flat range can still beat a primary-key seek, and a
unique-key union or common guard can beat a nonunique equality.

Literal arrays, parameters, and arithmetic values are checked structurally. The
recognized `ITEMS(values) ANY = field` form keeps its sequence semantics: binary
values enumerate bytes, while scalar `field IN binary` compares the whole binary
value. Field-based ANY/ALL predicates retain their multikey semantics.

Nested AND/OR expressions on one scalar key use interval union and intersection,
and compatible conditions from separate WHERE clauses are intersected too. These
operations walk ordered disjoint sets without distributing the Boolean tree into
conjunctions. Direct AND bounds still prefilter IN lists before producing point
intervals. The same purity proof and 64-node analysis budget apply before any
bound is evaluated.

Conjunctions collect direct bounds across WHERE clauses, then pass the allowed
intervals into nested branches before expanding membership values. Sibling ORs
without membership run first, so their bounds can discard keys before sorting
or allocating point intervals. Empty contexts still validate subsequent values:
throwing arithmetic or invalid bindings must retain the original filter fallback.
Small interval sets use binary membership checks before building ordered sets.
Larger contexts compare binary-search work against an ordered merge; broad
membership intersections filter sorted keys without searching every input key
through another large interval set. The all-values interval skips filtering.

The planner retains each combined candidate's predicate coverage so a weaker
scan of that key cannot displace it merely because it has fewer seeks. Candidates
on other indexes still compete by cost; predicates disappear from the residual
filter only when the selected scan enforces them. For ordinary Contains clauses,
query-local references to the expression before normalization preserve its
structural proof and its own parameter bindings. No physical plan is cached.

Secondary-index queries opened for update retain document-address filtering even
for scalar or unique indexes: updating a key can move a document into a later
range of the same scan. Read queries retain the one-key-per-document shortcut.
Primary-key scans need no additional tracking because UpdateMany preserves IDs.

This analysis has a 64-node budget and requires a matching scalar index. Includes,
computed keys, array selectors, mixed fields, and volatile or unrecognized function-call bounds
keep their existing plans. Arithmetic failures preserve the original filter and
its execution-time errors and short circuits. If another predicate selects a
cheaper index, the OR remains a filter. See steps 41–44 in the benchmark report.

The engine inspects the same expression nodes for LINQ and SQL. Equality ORs on
one scalar indexed expression become ordered IN seeks. OR branches with equivalent
leading scalar equalities can use that shared indexed guard while retaining the
entire OR as a residual filter. Guard extraction is bounded and skips includes;
it does not move later conditions ahead of throwing or volatile expressions. Separate scalar index
bounds and IN/BETWEEN constraints are intersected before index selection, with
only the scan-enforced filters removed. Contradictory bounds on a selected index
produce an empty index range, while a constant-false guard produces an empty
pipeline input. Constant Boolean guards expose indexable predicates while
respecting short circuits and volatility.
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
Proven scalar member paths ignore field-name casing, just as BSON lookup does.
The shared IR proof accepts up to 64 literal MEMBER_PATH accesses rooted in the
document, without parsing stored index definitions. This identity applies to
predicate matching, range/OR combinations, ordering, and grouping. Nested paths
still load documents for projections; the index-only loader retains its canonical
root-field proof. Other expression text still matches exactly, so case-sensitive
literals in computed indexes and array selectors remain distinct.

INCLUDE can replace stored reference members with values from another collection.
The planner keeps filters and sorting for affected paths instead of consuming
them with stored index keys. Proven disjoint paths remain indexed: including
`Owner.Manager` does not change `Owner.Score`. Computed and array paths use their
root-field dependencies conservatively. Even reference metadata can be supplied
by an included document, so it is not assumed immutable.

Range unions, nested Boolean intersections, and common leading OR guards use the
same per-path INCLUDE dependency check. An unrelated include does not disable
these candidates. A common guard narrows the stored rows while its residual OR
still evaluates resolved members. When a complete range predicate disappears,
the existing pipeline can also defer an unrelated sibling include until after
filtering and pagination. Candidate lookup and root-dependency checks use direct
loops to avoid captured predicates and boxed set enumerators.

Indexed not-equal predicates scan in index order and use an exclusive skip-list
seek to jump past equal keys. Comparison uses the database collation, and multikey
results retain one output per document. Their cost estimate remains unchanged.
Exclusive range starts use the same seek to skip duplicate boundary keys; inclusive
range endpoints retain their existing traversal. Scalar range evaluation also uses
the execution collation, keeping unindexed and residual comparisons consistent
with indexed ranges and ANY/ALL predicates. Traversal loop guards retain their
checks while creating diagnostic argument arrays only on failure.

## Boolean predicate results

Scalar comparisons, IN/LIKE/BETWEEN, their ANY/ALL variants, and AND/OR expressions
reuse two internal immutable Boolean values. This avoids allocating a BSON wrapper
and boxed Boolean for each predicate result. The computation still uses the active
collation, current parameters, and the existing short-circuit order. Only plain
Boolean results are shared; projected documents and arrays remain independently
owned, and public BsonValue constructors and conversions retain their behavior.

## LIKE character evaluation

LIKE compares individual UTF-16 code units using the execution collation. Its
comparison helper reads one-character ranges of the existing strings instead
of allocating two temporary strings at each comparison. SQL LIKE and ordinary
LINQ Contains/StartsWith/EndsWith use this path, including residual predicates
and full index LIKE scans. A terminal `%` accepts the remaining value immediately.
Otherwise the matcher consumes the entire value: `%` accepts zero or more UTF-16
units, `_` accepts exactly one, and literal NUL is distinct from pattern exhaustion.
Retries advance the input start after the most recent `%`, ensuring finite
progress without recursive calls or scratch allocations. Difficult patterns can
still require repeated suffix comparisons; this is not a linear-time guarantee.
Differential tests compare the character helper with isolated strings across every
UTF-16 code unit and compare wildcard results with an independent dynamic-programming
reference in multiple collations. Indexed prefix candidate selection is a separate
path and retains its existing behavior.

## Limited sorting

For residual ORDER BY with a positive limit and `offset + limit <= 1024`, a
bounded maximum heap retains only the best requested keys and reload addresses.
Comparison uses the active collation, each segment's direction, and input order
to break ties. Every input key is still evaluated and checked against the sort-key
size limit. Larger or unbounded requests retain the disk-capable sort path.
When that path produces multiple sorted blocks, a heap merges their next keys.
It retains the active block on collation ties and uses original block order for
other ties, preserving the previous output order. Identical-key runs and the
single-block path keep their shortcuts.
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
[`query-ir-benchmarks.md`](https://github.com/litedb-org/LiteDB-Artifacts/blob/main/pull-requests/2905-shared-query-ir/query-ir-benchmarks.md) for measured results and reproduction details.

## Extension boundary

The internal factories are the construction boundary for a future opt-in source
generator. Generated code can build a logical template and bind parameter values;
it must still enter `QueryOptimization` to select a plan at execution time.
A supported public generated-template surface and specialized materializers remain
separate follow-ups. This change preserves canonical `Source` identity for
index matching and the bounded compiled-delegate cache. It introduces no physical
plan cache or experimental call-site interceptors.
