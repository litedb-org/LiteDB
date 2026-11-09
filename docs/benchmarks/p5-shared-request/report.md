# P5-SHARED-REQUEST

Measurement work in progress. Orchestrator instructed report-only per-request Shared comparisons against NuGet 5.0.21, twice on Linux x64, Windows x64 and macOS ARM64, net8/net10. No production changes or existing threshold changes.

Product baseline verified by `git ls-remote origin refs/heads/v6/phase4`: `7000dd6f01d0187a8fb5d030064eb327864ab731`. Scratch harness starts at Shared-open report `3b1f280b69c44a90add2948f6968de921f57bb5b`.

## Method

One scratch-only workflow supplies six legs: Linux x64, Windows x64 and macOS ARM64, net8/net10. Each leg builds NuGet `[5.0.21]` and an independent checkout of exact phase4 `7000dd6f01d0187a8fb5d030064eb327864ab731`, Release, `TestingEnabled=false`. No PROFILE instrumentation. Tiered compilation is disabled for optimized first JITs. The v6 one-file and durable defaults and the default 1,000-page checkpoint are asserted. v5 Shared has no equivalent per-commit durable setting; this compares defaults, not equal durability guarantees.

Each leg runs two repetitions, each with six alternating v5/v6 rounds per scenario and concurrency. Each sample uses a fresh 1,000-row database with 96-character payloads; each worker warms the exact operation ten times on its own separate database, then waits at a common filesystem barrier. The timed loop is 100 complete `new LiteDatabase(shared) -> work -> dispose` requests per worker. Four-process samples use one shared file; there is no persistent anchor connection. All four measured worker intervals must overlap. This establishes overlapping request loops, not simultaneous lock ownership at every instant.

(a) finds one unchanged row by `_id`; (b) inserts one uniquely numbered row with an integer payload; (c) reads ten 96-character rows by `_id`, then updates one row owned by that process. Each operation is auto-committed; the mixed request has no explicit encompassing transaction. After every sample an independent reopen checks every seed payload/value and every inserted identity/value. There are no engine, format or safety machinery changes in this step; verification guards benchmark correctness and is not a new crash-safety qualification.

Primary latency is the arithmetic mean of worker elapsed/request values, followed by the median across the six rounds. This includes open, work, dispose, and in-loop result assertions. Startup, seed, warmup, barrier wait, and final verification are excluded. A secondary coordinated wall-time/request metric records aggregate throughput (including barrier release and polling overhead); it is retained separately in JSON and must not be interpreted as individual request latency. Bounds are observed sample min/max, and conservative ratio bounds `min(v6)/max(v5)` to `max(v6)/min(v5)`; they are not confidence intervals or latency percentiles. Repetitions, runtimes and platforms are never pooled.

Audit: `python docs/benchmarks/p5-shared-request/summarize.py <downloaded-artifacts> <output-directory>`. It requires all 864 samples, exact binary/source identity within each leg, expected architectures/runtime, all verification markers, and every round. Raw samples retain wall time, runtime, binary hashes, source SHAs and process intervals.

## Remaining open cost and candidates

The [Shared-open report](../p5-shared-open/report.md#cause-and-profile-evidence) contains the completed Linux/Windows net8 EventPipe profiles. Their diagnostic timings include observer overhead and overlap; these are neither additive budgets nor new request measurements. No macOS or net10 profile was captured there.

| Prior diagnostic ms/open-close | Linux x64 | Windows x64 |
| --- | ---: | ---: |
| Whole cycle | 2.435–2.534 | 3.104–3.199 |
| Protected registry attach, inclusive | 0.913–0.959 | 0.965–1.007 |
| Final close, inclusive | 0.395–0.465 | 0.621–0.645 |
| Canonicalization, all calls | 0.110–0.127 | 0.639–0.675 |
| External native admission, inclusive | 0.220–0.249 | 0.285–0.321 |
| Map disposal, both views | 0.051–0.058 | 0.078–0.087 |
| Volume checks, all calls | 0.007–0.009 | 0.018–0.022 |

Each cycle still performs seven physical opens, eighteen canonicalizations and five filesystem qualifications. Quiescent reuse was already active; allocation/reset and recovery sync did not dominate. Native DMS/WRITE acquisition was below 0.020ms/cycle in the earlier Windows trace. Eliminating locks or durability checks is not justified by that profile.

Candidate ranking, to investigate only; no implementation or claimed measured gains:

1. **Carry validated canonical identity through guards within the same admitted handle lifetime.** Estimate: avoid roughly half to most duplicate canonicalization, about 0.05–0.10ms on Linux and 0.3–0.6ms on Windows per isolated request. Those estimates are fractions of the prior diagnostic span, not predicted portable speedups. Preserve external facade admission and fresh identity checks at replacement boundaries; retain the actual native owner/handle, and reject aliases and changed generations. Require alias, rename/replacement, mixed-open exclusion, process-death and teardown interleaving tests. A pathname/global cache would not provide this safety argument.
2. **Consolidate repeated protected attach inspections and physical opens under one existing ownership scope.** Exploratory budget: 0.2–0.5ms Linux / 0.2–0.6ms Windows; low confidence until a finer profile separates nested work. Do not add this estimate to candidate 1: spans overlap. Preserve independent first-opener DB/WAL recovery, ephemeral envelope validation, writer/read admission order and mapping lifetime; test partial bootstrap, repeated recovery, last-close/open races and process death at each transition. Do not retain an idle implicit participant across disposal.
3. **Reduce duplicate final-close inspection/handle setup.** Exploratory budget: 0.1–0.2ms Linux / 0.1–0.3ms Windows, bounded by the inclusive 0.4–0.65ms final-close span. Keep final checkpoint, durable barriers, generation retirement and failure cleanup in order; require checkpoint-failure, interrupted close, repeated reopen and concurrent last-close tests. Removing final-close duties or deferring durability is outside this candidate.

The first candidate is the clearest measured duplication, especially on Windows; by itself it is unlikely to bring a large request regression down to 1.5x. Gains on macOS/net10 and under four-process contention cannot be inferred from the available profile. Keeping a database facade alive across requests would amortize lifecycle cost, but changes the requested application pattern and is not an implemented fix here.
