# Shared reads: profiling PR #3003 against pre-`bef9aa`

[PR #3009](https://github.com/litedb-org/LiteDB/pull/3009) builds on
[PR #3003](https://github.com/litedb-org/LiteDB/pull/3003). It removes repeated
startup and materialization work in ordinary shared mode. Existing callers get
the changes with default settings; no coordinator or experimental API is required.

## Comparison and measurement protocol

The three builds are pre-storage-stack `bef9aa^` (`0fd277aae`), PR #3003
(`137a1d934`), and this candidate (`459e525d1`). The benchmark runner is from
`81e28693c`. Raw results record full commits and the loaded DLL SHA-256, and the
runner refuses test-hook assemblies.

The host is Ubuntu 24.04.3, AMD Ryzen 9 3900X, x64, ext4 on LVM. .NET 10.0.11
and 8.0.30 were measured separately using Release production assemblies built in
isolated worktrees. Other database/fuzz workloads were active on the host; these
are not isolated hardware limits or Windows performance results. No tests, builds
or profilers from this investigation ran concurrently with the final timings.

The [runner](../tools/SharedReadBenchmarks/README.md) creates a fresh database of
2,000 documents containing integer IDs/values and a 200-character payload, under
invariant current culture. Every read checks the full result. Mixed work performs
one update per nine reads and verifies the final contents against an independent
expected-value array. Each run uses a fresh process and database. Three rounds
interleave builds and reverse their order in the middle round.

Two process lifetimes are reported:

- **Short run:** at least 1,000 point operations or 20 scans of warmup, then 8,000
  measured point operations or 150 scans. This captures initial JIT-tier costs;
  it is not cold process startup.
- **Steady state:** at least ten seconds of warmup, then 20,000 point operations,
  1,000 scans or 2,000 mixed operations. Ten consecutive timing-window means are
  retained to expose tiering/load changes. Direct-mode scan controls use this
  same protocol with the PR baseline and candidate.

Tables show medians of three run means. Negative changes mean less elapsed time;
percentages compare the candidate with the named baseline. Tail percentiles are
medians of each run's p99, not a pooled percentile. Full ranges, process CPU,
allocation, timing windows, first-operation and close costs are in the artifacts.

### Short-run latency

| Runtime / workload | Pre-`bef9aa` ms | PR #3003 ms | Candidate ms | vs pre | vs PR |
| --- | ---: | ---: | ---: | ---: | ---: |
| .NET 10 point | 0.2103 | 0.4152 | 0.2227 | +5.9% | -46.4% |
| .NET 10 scan | 8.2688 | 10.6275 | 8.4372 | +2.0% | -20.6% |
| .NET 8 point | 0.2417 | 0.4522 | 0.2380 | -1.5% | -47.4% |
| .NET 8 scan | 8.4257 | 10.7930 | 8.6055 | +2.1% | -20.3% |

### Steady-state latency

| Runtime / workload | Pre-`bef9aa` ms | PR #3003 ms | Candidate ms | vs pre | vs PR |
| --- | ---: | ---: | ---: | ---: | ---: |
| .NET 10 point | 0.0977 | 0.1656 | 0.1076 | +10.1% | -35.0% |
| .NET 10 scan | 2.4509 | 2.8061 | 2.4432 | -0.3% | -12.9% |
| .NET 10 mixed | 1.1923 | 1.0191 | 0.9201 | -22.8% | -9.7% |
| .NET 8 point | 0.1200 | 0.2082 | 0.1147 | -4.5% | -44.9% |
| .NET 8 scan | 2.7810 | 3.3013 | 2.8595 | +2.8% | -13.4% |
| .NET 8 mixed | 1.2521 | 1.0538 | 0.9363 | -25.2% | -11.1% |

### Tail latency and resource costs

| Runtime / workload | Pre p99 ms | Candidate p99 ms | Pre allocated KiB/op | Candidate KiB/op | Pre CPU ms/op | Candidate CPU ms/op |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| .NET 10 point | 0.4128 | 0.4513 | 270.7 | 261.9 | 0.0995 | 0.1433 |
| .NET 10 scan | 5.2955 | 5.4412 | 4732.4 | 4408.8 | 2.2725 | 2.4638 |
| .NET 10 mixed | 10.5975 | 5.7110 | 271.4 | 342.1 | 0.1983 | 0.4191 |
| .NET 8 point | 0.5165 | 0.5295 | 270.9 | 262.1 | 0.1230 | 0.1540 |
| .NET 8 scan | 5.6548 | 5.9290 | 4732.5 | 4540.1 | 2.6900 | 2.9100 |
| .NET 8 mixed | 10.7118 | 5.9873 | 271.6 | 342.3 | 0.2500 | 0.4400 |

Direct-mode scan controls (median of three run means):

- .NET 10: PR 1.3906 ms → candidate 1.2755 ms (-8.3%); allocated bytes/op 3,764,697 → 3,013,353.
- .NET 8: PR 1.7380 ms → candidate 1.4961 ms (-13.9%); allocated bytes/op 3,764,759 → 3,141,415.

Steady-state shared-read elapsed time is near the pre-stack baseline in this
fixture, with a remaining .NET 10 point-read gap. Point-read CPU and mixed-workload
CPU/allocation remain higher than pre-stack. Tail latency does not improve in
every case, and a mean improvement should not be read as a universal p99 win.
The detailed run ranges and windows are retained in the raw summary.

Final close remains more expensive than pre-stack:

- .NET 10 mixed: pre 0.091 ms, PR 23.314 ms, candidate 24.085 ms per connection close.
- .NET 8 mixed: pre 0.066 ms, PR 23.812 ms, candidate 23.188 ms per connection close.

This close occurs once after 2,000 measured mixed operations. All final runs
passed full-content verification and had zero WAL content after final close.
Per-run before-close WAL/data sizes are recorded; peak disk usage was not sampled.
The fingerprint table retains at most 64 small immutable entries per process;
the query-local field-name table retains at most 32 strings of at most 64 ASCII
characters. Neither table retains database pages or streams.

## Why the gaps appeared

The regressions have several causes; page checksums alone do not explain them.

1. **Repeated collation fingerprints.** Shared operations construct fresh engines,
   and an open can validate the same runtime collation several times. Each uncached
   fingerprint performs 361 culture comparisons and hashes the answers. In the
   original point-read trace, this occupied 26.2% of sampled main-thread time; the
   bounded cache reduced that to 0.6% (0.2% in uncached work). An isolated early
   comparison improved point reads by 22–25% across .NET 8/10.
2. **Checksum code spent too long in the initial JIT tier.** A fixed warmup of
   1,000 calls was insufficient to establish steady state. CRC and span helper
   overhead remained in CPU profiles even when safe-point-biased managed samples
   barely named CRC. Optimizing the CRC methods from their first call cut the
   short .NET 10 point-read mean from 0.2989 to 0.2255 ms in the three-round
   experiment. The polynomial, bytes checked and hardware/portable algorithms
   are unchanged. Longer warmup is now reported separately.
3. **Duplicate startup work.** Disk startup validated and parsed the complete
   header, then the engine read and parsed it again. Transferring that validated
   header within the same open reduced the point-read mean by about 6.5% in the
   isolated experiment. Probing a normally absent rebuild marker also threw a
   first-chance exception on every open; an absence-aware `FileInfo` probe
   removed about another 3% while retaining access/error rejection.
4. **Per-document materialization.** After the startup improvements, an attached
   steady-state CPU profile put 28.2% of samples under BSON `ReadDocument` and
   18.6% under element decoding (inclusive, overlapping percentages). A query now
   reuses its resettable reader and schema callback and retains a small cache of
   short ASCII field names. Scan allocation fell from approximately 5.30 MB to
   4.51 MB per operation in the intermediate controlled comparisons. The field
   name cache alone showed an allocation reduction, not a reliable latency gain.

The individual experiments used intermediate revisions and different process
lifetimes; their percentages must not be added together. The final comparisons
above use the complete candidate and identified production assemblies.

## Remaining costs and limits

Shared mode still constructs engines, opens files, restores WAL state and creates
query state. Streaming reads first try a bounded buffer (100 values/64 KiB), then
restart on a leased snapshot engine when it overflows. The discarded prefix and
second engine remain real costs. This patch does not remove replay: another
process can write reusable WAL slots without changing the data header, so header
identity alone is not a safe cache-coherence fence.

Lazy checkpointing improves writes but can leave WAL work for subsequent reads.
In the exploratory mixed trace, restoration appeared in 15.1% of sampled thread
time and durable native flushes in 49.5%. These include waits. Final close is
measured separately, and every final benchmark verifies that the closed database
has no remaining WAL content; deferred work is not silently omitted.

The optional coordinator was also profiled using a separate process and 2,000
cursor updates. An attached trace placed 99.6% of sampled client time in protocol
reads. The per-document protocol has several exchanges; reducing them is a
plausible optimization. Client waiting also includes server execution and durable
commit, so this does not prove that transport alone explains the gap. Coordinator
behavior is unchanged, and none of the reported gains requires enabling it.

Managed `dotnet-sampled-thread-time` samples include waiting and are biased toward
safe points; inclusive percentages overlap. CPU `perf` profiles were used to
check the JIT/materialization theories. Some native runtime frames remain
unresolved. Traces are diagnostic evidence, not instrumented benchmark speedups.
An unsuccessful launch-traced coordinator attempt and a scan profile without
usable managed maps are excluded.

## Safety boundaries and validation

| Change | Safety boundary and discriminating coverage |
| --- | --- |
| Collation cache | At most 64 immutable entries keyed by comparer name, LCID, options and sort version/ID; collisions recompute. No database state is cached. Every loaded header still validates its persisted stamp. Culture/options, concurrent collisions, changed stamps, and plain/encrypted native indexes are covered. Unsupported sort-version lookup retains the original zero-result/retry behavior. |
| Initial header transfer | Only the header validated by that particular `DiskService` open is transferred, once. New opens revalidate. File-backed short reads, injected I/O/EOF failures, repeated retries, unchanged bytes, header corruption and caller-owned streams are covered. |
| Marker probe | Missing markers avoid exceptions; inaccessible or present markers still block opening/creation. Tests cover a subsequently created marker, missing parents, denied access and dangling symlinks on Unix. |
| CRC JIT policy | Existing checksum algorithms and persisted bytes are unchanged. Published CRC vectors, differential tests and file-backed corruption tests pass with hardware intrinsics disabled. |
| Reader/name reuse | Reader and name table belong to one query. A `finally` clears borrowed segments after success or failure. Names are bounded to 32 short ASCII strings, with full-byte verification on hits; Unicode/long names use the existing strict decoder. Legacy/schema and plain/encrypted multi-page documents, projections, continuation-read failures, retry, retained-buffer collection, collisions and every segment boundary are covered. |

The complete nine-partition suite passed on **.NET 8.0.30 and .NET 10.0.11**:
**4,499 passed, zero failed, seven existing skips per runtime**, excluding 16
repeated runtime/test-hook guards. Raw partition totals are 4,515 passed and seven
not executed. All 2,121 test methods were assigned to exactly one partition.
Tests use Release with `TestingEnabled=true`; benchmarks use separate production
assemblies with `TestingEnabled=false`.

Additional evidence:

- All target frameworks compiled, including net462/net481; those legacy runtimes
  were not executed locally.
- 68 focused startup, recovery, compatibility and cursor cases passed on each
  modern runtime after the final source restoration/build.
- 26 checksum cases passed on .NET 10 with hardware intrinsics disabled.
- Actual LiteDB 5.0.21 files passed the create/migrate/refuse/verify compatibility
  script, including plain/encrypted, binary/culture, data/WAL, collision refusal
  and `LIMIT_SIZE` fixtures.
- Negative controls deliberately removed reader cleanup, restored duplicate
  header reads and restored exception-based marker probes. All four targeted
  cases failed as expected; the final source was restored and tests rerun.
- The benchmark's production-assembly guard was checked against a test-hook
  assembly and correctly rejected it. The compatibility helper rebuilds its
  checkout with test hooks; production assemblies were rebuilt and all six hashes
  checked again before the final comparison.

The tested and measured library source is `459e525d13ea6b0a5dc88d6dcb0cbf581561ad02`.
Later commits only change the benchmark/report. This is Linux performance and
local validation evidence; Windows performance has not been measured here.

## Raw artifacts

[Reproducible measurements, traces and validation logs](https://github.com/litedb-org/LiteDB-Artifacts/tree/7a22a3247351609834da0d84990cd73950012a3e/pull-requests/3009-shared-read-performance) include the
102 final benchmark runs, production hashes, runner source, orchestration,
incremental experiments and test results. Deliberate negative controls and early
fixture errors are labeled separately from final passing validation.
