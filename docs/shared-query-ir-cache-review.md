# PR #2944 cache review

This focused code review replaces the unavailable human cache review at the
maintainer's request. It covers `LinqExpressionCache`, `LinqQueryShape`,
`LinqBindingEvaluator`, mapping guards, every translator `Evaluate` call, and
the SQL/text cache binding boundaries. It is not a new audit of every optimizer
or storage change in the PR.

## Findings and fixes

- Closed-element detection matched method names without checking their owner.
  `Math.Min` and `Math.Max` in branches containing the server clock were rejected
  as captured sequence accesses. Sequence-name matching now requires Enumerable,
  Queryable, or an enumerable instance. Indexers and array access remain guarded.
- The LINQ shape key omitted array and member-initializer child counts. Different
  nesting could produce the same preorder tokens, reusing an incorrect projection
  or compiled capture evaluator. Counts now distinguish those shapes.
- A mapping guard checked that the old member remained in the list, but an earlier
  inserted member could become the translator's selection. Guards now validate
  the selected member using the same precedence as translation.
- Cache hits serialized captured `BsonValue`s through the mapper, whereas initial
  translation passed them through unchanged. Hits now use the same pass-through
  rule, including for custom mapper overrides.
- The captured Min/Max Date regression now asserts both parameter values.

The initial regression runs reproduced the four Math branch failures, four
array/initializer projection collisions, mapping-precedence failure, and BSON
serialization-path difference before their respective production fixes.

## Value-dependent translation inventory

| Evaluated input | Cache treatment |
| --- | --- |
| Ordinary constants, captured members, helper calls, closed element operations | Current expression slots are reevaluated and serialized in translation order. |
| Array, dictionary, list, and explicit index arguments embedded in Source | Shape visitor rejects ArrayIndex, get_Item, and IndexExpression shapes. |
| StringComparison selecting ordinal equality IR | Both ordinal and nonordinal paths record an uncacheable binding. |
| Enum numeric/name conversion | Synthesized constants cannot match original binding slots; translation is not cached. |
| Enum.Equals captured runtime type | Wrong/null types explicitly mark the result uncacheable; matching values use synthesized constants. |
| Captured DbRef values and generated reference metadata | Synthetic bindings bypass shape reuse; null/value transitions are covered. |
| Invoked expressions | Unsupported by the shape visitor; use the translator and invocation expander. |

Resolver selection, unary dispatch, quantifier shortcuts, and server-runtime
validation depend on expression structure and CLR metadata, not captured values.
The mapper's enum setting and predicate/index mode are checked independently of
the structural key. Runtime serializers remain live on each bind.

## Ownership and publication

Shape tokens contain CLR metadata and ordinals, not closures or captured values.
The pooled visitor clears expression references in `finally` and permits reentry.
Compiled capture evaluators substitute current-tree constants by occurrence and
preserve reflection/DynamicInvoke exception wrapping. Published buckets are
immutable and atomically replaced, with full token equality after bucket lookup.
Templates hold independent empty bindings; returned bindings copy mutable node
metadata and field sets. Existing concurrency, reentry, retention, repeated-slot,
exception-chain, and bounded-cache tests exercise these properties.

SQL and text caches use exact text keys and bounded admission. Their templates
strip caller parameters, copy query clauses for execution, and retain no physical
plans or results. SQL limits and offsets are text literals, not captured values.
The existing cache tests cover rebinding, grouping, volatility, collation,
concurrency, and retained-object checks.

## Added regression coverage

`LinqCacheStructure_Tests` compares both cache warmup orders and repeat hits with
direct, compilation-cache-disabled translation for nested array/initializer
projections and compiled helper captures. It also checks conditional, coalesce,
AND, and OR Math branches while preserving NOW(), and rejects unsafe empty
Enumerable/Queryable extrema before evaluating them.

`LinqCacheRebinding_Tests` covers both string equality forms and warmup modes,
enum runtime-type transitions under both storage settings, mapping precedence,
raw BSON serialization parity, and null/changing DbRef captures.

## Final local verification

Production/test source revision `b1e027307` passed the Release solution build
with `TestingEnabled=true` (zero errors) and the complete local suites under
`TZ=Europe/Berlin`:

- net8.0: 2,990 passed, 7 existing skips, 0 failed (2m21s).
- net10.0: 2,990 passed, 7 existing skips, 0 failed (2m01s).
- C# size and whitespace checks passed.

These totals include 23 new regression cases. CI was not awaited, as explicitly
requested by the maintainer; these results do not claim final CI success.
