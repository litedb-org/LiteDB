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
  With tiered compilation, inspect consecutive timing windows and allow enough
  warmup time; a fixed operation count can still measure instrumented tier-0 code.
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

## Contended acquire

A change to a wait primitive, a lock handoff, Shared coordination or engine
services is compared with its base by the writer-contention step of the
shared-slot performance workflow. That step runs for PRs touching
`LiteDB/Client/Shared/**` or `LiteDB/Engine/Services/**`.

`scripts/measure-shared-contention.py` runs the
[contended-acquire scenario](../../tools/SharedReadBenchmarks/README.md#contended-writer-acquisition)
in the same five alternating rounds as the two- and four-writer contention
workload. Each process runs a fixed number of explicit transactions and stamps
arrival, acquisition and release on the host's monotonic clock.
`.github/scripts/compare_contention.py` compares the round medians against
stated tolerances; this is performance evidence, not exact equality.

- **Gated:** p99 acquire latency, maximum wait, the hand-off gap after a
  release, the overtaking rate (a later arrival acquiring while an earlier one
  still waits) and the most acquisitions by other processes during one wait.
- **Reported only:** waiter age (how long the oldest waiter has waited at each
  acquisition) and per-process progress intervals.

State the tolerance in the PR when you change it. A poll shows up in the
hand-off gap. A barging owner shows up in overtaking, others-while-waiting and
maximum wait, even though p99 alone can improve. While `.github/safety/net-modes.json`
keeps the diff nets advisory, findings are reported and the step passes.
Unverified runs fail regardless.

Existing runners: [QueryIrBenchmarks](../../tools/QueryIrBenchmarks/README.md),
`tools/QueryOptimizationBenchmarks`, `tools/QueryParameterLifetimeBenchmarks`,
[MemoryValidation](../../tools/MemoryValidation/README.md), and
[MemoryProfiles](../../tools/MemoryProfiles/README.md).
The [memory validation report](../memory-management-validation.md) illustrates
explicit baselines, retained-memory checks, and documented latency/WAL tradeoffs.
