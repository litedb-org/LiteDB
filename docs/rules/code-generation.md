# Source generation and AOT

Use this when working on generator/AOT branches. These are review lessons, not
a claim that every checkout contains a source generator or supports every AOT runtime.

- Incremental generator cache models need value equality for all semantic inputs.
  `ImmutableArray<T>` alone does not give element-wise equality. Do not retain
  syntax nodes, symbols, or locations in semantic models unless output depends
  on them. Whitespace/location-only changes should not regenerate unchanged code.
- Separate code-generation inputs from diagnostic location tracking. Verify
  incremental invalidation with tracked generator steps; equal emitted text alone
  does not prove the pipeline avoided recomputation.
- Keep generated models/diagnostics organized with namespaces and small focused
  types. Partial declarations are appropriate where generation requires them;
  unrelated implementation splitting is not an incrementality improvement.
- AOT attributes and a normal JIT test run are not proof of AOT readiness. Pack
  the actual NuGet, consume it from an external project, publish/run the native
  binary, and verify generated registrations and analyzer packaging. Record which
  runtimes/platforms were actually exercised.
- Runtime capability probes must exercise the delegate shapes LiteDB uses.
  Successful `Func<int>` interpretation does not prove custom delegate support.
  Test documented fallback switches in a fresh process before static initialization,
  as well as forced internal fallback tests.
- When adding interpreter/JIT mode selection to deferred delegates, capture the
  selected mode when creating the factory. Reading ambient mode only at first
  execution can contaminate later cache hits.
- Compare fallback evaluation with normal execution across captured lambdas,
  invocation, nullable boxing/coalescing, numeric/user conversions, side effects,
  short circuits, and exception chains. Unsupported cases should fail explicitly,
  not silently drop operators or freeze volatile values.

See [validation](validation.md) for runtime/packaged-artifact evidence and
[query expressions](query-expressions.md) for binding semantics.
