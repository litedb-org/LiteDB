# Query expressions and caches

Use this for LINQ/SQL translation, binding, and reusable templates. Architecture
and differential-test setup: [shared query IR](../shared-query-ir.md).

## Translation and observable semantics

- LINQ and SQL share `BsonExpressionFactory`. LINQ bindings construct nodes
  directly, without tokenizing templates or parsing generated text. Preserve
  canonical `Source`: persisted indexes and delegate caches depend on it.
- Propagate `IsVolatile` through every factory and binding. `IsImmutable` alone
  does not distinguish parameters from volatile functions. Preserve `IsANY`
  when copying predicates; generated delegate text cannot recover it reliably.
- LINQ MAP/FILTER includes selector immutability and source usage in its metadata.
  Explicit SQL MAP/FILTER retains its historical input-only metadata for those flags.
- Nested evaluators receive the caller's parameter document explicitly. Embed
  unbound nested templates in compiled delegates so caches cannot retain an old
  caller's parameters or large serialized values.
- Preserve captured evaluation order, count, short circuits, conversion semantics,
  and reflection exception chains. Do not freeze `NOW()`/`GUID()` inside a closed
  CLR subtree or evaluate an unchosen throwing branch during binding.
- When classifying captures or adding resolvers, check the declaring contract and
  signature where they determine semantics: `Enumerable.Min` differs from
  `Math.Min`, and a hidden `ContainsKey` need not implement the dictionary contract.
  Test user-defined conversions, nullables, lambdas, and nested collection calls.
- Existing literal query helpers must keep self-contained `Source`/`ToSQL()`
  round trips. Use the opt-in parameterized helpers when values require separate
  bindings; composition must not alias mutable operand parameter containers.

## Cache ownership and keys

Cache reusable structure, never current bound values. Bound calls must get current
parameters, independent mutable nodes/field sets, and no retained closures.

| Cache | Lifetime and admission |
| --- | --- |
| Automatic LINQ reuse | Mapper-local and bounded |
| Public text expressions | Process-wide; bounded by key count and length |
| SQL SELECT templates | Database-local; bounded by statement count and length; first use retains only a key, recurring use promotes an unbound template |

For LINQ shapes, include child counts for variable-arity arrays/initializers to
prevent sibling-layout collisions. If a runtime value changes IR structure or
`Source`, encode that distinction in the key or bypass caching. Validate publicly
mutable mapping metadata and selected-member/list precedence on every hit, not
merely whether a formerly selected member still exists. CLR evaluators read all
constants from the current shape and defer compilation until reuse.

For parsed expressions, capture templates only after parsing and EOF validation
succeed. Preserve explicitly null bindings. Tokenizer entry points must still
consume their input; disable parsed-template reuse when compilation caching is
disabled in tests.

For SELECT hits, copy every clause and bind current parameters. Cache no physical
plans, results, engine state, or caller parameter documents. `TextReader` keeps
its streaming parser path. Bound root projections preserve GROUP BY behavior
without relying on singleton identity or overwriting predicate parameters.
Built-in aggregate templates need separate bindings because GROUP BY writes its
key into the parameter document.

## SQL aliases

Keep explicit `AS` aliases distinct from inferred projection names and explicitly
root-qualified member paths. Alias identity and duplicate handling must agree
with BSON's case-insensitive lookup. Carry parsed nodes/provenance instead of
reparsing generated projection text, which can lose quoted names and literals.
Compile referenced aliases and their nested expressions only as needed; inspect
embedded expression templates for volatility as well as the outer tree.

## Verification

Use `DirectTranslationScope` to prohibit tokenizer creation and bypass cached
delegates in differential tests. Exercise A/B/A values, structural collisions,
mapper mutation, concurrent callers, nested expressions, and weak-reference
collection of closures/large values while the cache owner remains alive.
Compare CLR execution where appropriate to avoid a shared-translator blind spot.
Measure production assemblies with [QueryIrBenchmarks](../../tools/QueryIrBenchmarks/README.md).
