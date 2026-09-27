# Query planning and execution

Use this for optimizer rewrites, indexes, INCLUDE, replay, LIKE, sort, and vectors.

## Rewrites must preserve results and failures

- Optimizer rewrites must use the execution collation for comparisons that can
  contain strings, including nested BSON values. Do not intersect separate ANY/ALL
  predicates as though they constrained one scalar value.
- Validate the complete pure Boolean shape before pushing bounds into membership
  branches. Separate OR shape validation from value evaluation; preserve leaf
  order and bindings, and avoid temporary branch lists for rejected shapes.
- Empty intervals must still validate subsequent bounds so invalid bindings and
  throwing arithmetic preserve filter fallback. A primary-key candidate must not
  bypass flat empty ranges when that changes residual errors.
- Cache only fixed query-local metadata, never bound values. Sharing Boolean
  results is safe only for plain immutable Boolean `BsonValue`s; projected
  containers and parameter documents stay independent. Keep short circuits and
  required type errors.

## Index identity and scan lifetime

- `CollectionIndex.BsonExpr` is lazy for persisted indexes: ordinary reads use
  canonical text and existing keys. Keep new-index validation eager; maintain
  keys and execute vector expressions through the property.
- Set `Index.SingleKeyPerDocument` only after matching a scalar IR expression
  or a canonically escaped scalar root-field path to the stored definition.
  Raw member names may resemble multikey or computed expressions; use the
  shared path formatter.
- Case-insensitive identity requires a proven scalar MEMBER_PATH chain rooted
  in the document, literal member names, and bounded depth. Root-only lookups
  still require canonical escaping. Never compare arbitrary expression text
  ignoring case: computed expressions can contain case-sensitive string literals.
- Multikey and unproven scans need document-address deduplication. Secondary-index
  queries opened for update need it even for scalar/unique keys: `UpdateMany`
  can move a document into a later scan interval. Test key-moving updates, read
  order, duplicate keys, page release, and rollback for new access paths.
- Deduplication and range rewinding must agree with the active collation.
  Distinct source values can still seek the same collation-equivalent key.

## INCLUDE and replay

INCLUDE can replace stored members, including reference metadata supplied by the
referenced document. Do not consume a filter or ordering with an index on an
affected path; retain indexes for proven disjoint paths. Apply the same dependency
test to Boolean ranges and common-guard candidates. A leading common guard is only
necessary, so retain the original OR filter if it still reads resolved references.
Resolve parent references before nested children and preserve these semantics in
projections, scored queries, and aggregate replay.

Replay addresses are loader-specific: `IndexLookup` uses an index-node position;
`DatafileLookup` uses a data-block address. Keep `RawId` consistent with the loader
that receives it during sort/aggregate replay.

## LIKE and temporary sorting

LIKE compares one UTF-16 code unit at a time using the execution collation.
Do not replace comparisons with ordinal checks or absorb neighboring surrogate
or combining characters. Consume the whole value, distinguish literal NUL from
pattern exhaustion, and make input progress on wildcard retries. Terminal `%`
accepts the remainder immediately. Compare changes against the independent test
reference, including `_` after `%` and repeated trailing characters.

Temporary sort keys use the extended string/binary headers from index pages.
Decode with `ExtendedLengthHelper`: lengths count UTF-8 bytes, can exceed 255,
and must preserve full non-length type codes, including vectors. During multi-block
merge ties, retain the active block first, then original block order. Test actual
spill and merge boundaries, not only in-memory order.

## Vector query composition

Keep scalar `VECTOR_SIM` semantics separate from API thresholds using an index's
metric. Copy vector filter identity and score/projection metadata when cloning a
query. Preserve computed expressions and quoted paths during index selection;
validate conflicting API calls before mutating the query. See
[vector scores](../vector-search-scores.md) and
[vector compatibility](../vector-query-compatibility.md).

Use `tools/QueryOptimizationBenchmarks` for per-change end-to-end measurements.
