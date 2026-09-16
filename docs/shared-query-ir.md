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
Collation is likewise supplied at execution time. There is no global translation
cache keyed only by CLR type; each translation observes its own mapper. Reuse a
template with the mapping under which it was translated. Rebinding stores no
index choice; creating or dropping an index still changes subsequent planning.

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
A supported public generated-template surface, structural expression fingerprints,
automatic mapper-aware shape caching, and specialized materializers remain
separate follow-ups. This change preserves canonical `Source` identity for
index matching and the bounded compiled-delegate cache. It introduces no physical
plan cache or experimental call-site interceptors.
