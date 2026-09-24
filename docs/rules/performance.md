# Performance evidence

Use this for optimization claims, benchmarks, cache budgets, and memory retention.

- Compare the actual baseline and candidate using production Release assemblies
  with `TestingEnabled=false`. Record commit/binary identity, runtime, OS,
  configuration, input shape, repetitions, and raw output. Keep builds isolated
  from tests that enable hooks.
- Measure representative end-to-end work as well as the hot helper. Verify result
  correctness during benchmarks. Explain which improvements existing callers get
  automatically and which require opting into an API or changing configuration.
- Measure incremental optimizations independently when attributing gains. Include
  regressions and tradeoffs: retained memory, allocations, startup, tail latency,
  WAL/disk growth, and throughput can move in different directions.
- Separate cold compilation/startup from warm reuse. Use comparable environments
  and fresh processes where static caches or retained memory affect the result.
  Do not combine measurements from different hosts/runtimes into one speedup.
- Memory tests should prove ownership release while the intended owner remains
  alive. Allocation totals, working set, cache capacity, and live pinned bytes are
  different measurements; a lower allocation count alone does not prove no leak.
- Checkpoint progress, reuse of WAL capacity, and shrinking the WAL file are
  distinct outcomes. Report the one actually measured, including reader lifetime
  and peak storage. Avoid describing availability gains as disk-space savings.
- Keep concise results in the PR/design document and large raw artifacts in
  `litedb-org/LiteDB-Artifacts` when publication is part of the task. Update stale
  benchmark claims after changing the implementation.

Existing runners: [QueryIrBenchmarks](../../tools/QueryIrBenchmarks/README.md),
`tools/QueryOptimizationBenchmarks`, `tools/QueryParameterLifetimeBenchmarks`,
[MemoryValidation](../../tools/MemoryValidation/README.md), and
[MemoryProfiles](../../tools/MemoryProfiles/README.md).
The [memory validation report](../memory-management-validation.md) illustrates
explicit baselines, retained-memory checks, and documented latency/WAL tradeoffs.
