# Memory-management correction validation

For the later shared-reader optimization and configurable memory profiles, see
[memory-profile-results.md](memory-profile-results.md). This report preserves
the measurements of the earlier correction revision.

Measured 2026-09-12 on Windows 11 (build 26200), AMD Ryzen 9 9955HX,
32 logical processors. Each workload ran in a fresh process, sequentially,
with production Release libraries (`TESTING` disabled). Raw results include
runtime versions and library SHA-256 values in [memory-measurements](memory-measurements/).
The independently added [original Linux measurements](memory-management-benchmark-results.md)
are preserved; they measure the original PR head on a different host and should
not be mixed numerically with this correction's Windows results.
The [runner instructions](../tools/MemoryValidation/README.md) reproduce the workloads.

## Compared implementations

- `baseline`: upstream `dev`, commit `47268cb4`, before this PR. Its original
  extensible transaction budget and cache behavior are unchanged; it has no cache setting.
- `prhead`: PR head `e75c3fda`, before these corrections, at 64 MiB.
- `current`: corrected implementation, at 8/64/256 MiB. The exact measured
  production binary is identified by the SHA-256 in every JSON report.

## What was corrected

Aggregate sources now dispose on completion, early cursor closure, and failures.
The original reproduction repeatedly grew temporary storage by 1.56 MiB per
`SELECT FIRST(*) FROM docs ORDER BY sort`; regression tests require reuse.
Cache-size parsing rejects invalid, negative, empty, and overflowing values and
accepts explicit zero. A bounded atomic transaction registry fixes concurrent
`$database` enumeration. `transactionPages` reports the safepoint counter;
it deliberately does not claim to be a pin count.

The compiled-expression cache uses 1,000 immutable entry slots with atomic
publication, shared by scalar and enumerable delegates. Source/type checks make
collisions a cache miss, never a wrong delegate. Transaction registration uses
100 atomic slots with a capacity reservation; diagnostics copy references before
reading the atomic transaction-page counters. Both paths have no monitor lock.
Stream and disk disposal admission uses `Interlocked.Exchange`.

The page cache retains one monitor for the combined pin, publication, eviction,
and segment-lifetime protocol. Disk reads and expression compilation stay outside
it. Within-target completion skips its trim lock, and the unpublished cache
constructor requires no lock. Released-page poisoning now only runs under
`DEBUG || TESTING`; production still zeroes writable pages before reuse.
Segment storage and eviction policy are separate composed components, rather
than one 923-line implementation. This is not a lock-free page-cache claim.

## Measurements

These are acceptance samples, not a statistical performance guarantee. CPU
scheduling, JIT, GC, and the OS page cache affect individual runs. “Warm scan” is
the second scan after checkpoint; the OS cache is not flushed. There are 2,000
lookup samples per worker and 100 release samples, so release p99 is noisy.
Vector timings include validation of returned IDs and vector contents.

### 200,000 documents

| Runtime | Implementation / cache | Warm scan ms | Lookup 16-thread p50 / p99 us | Normal release p99 us | Overflow release p99 ms | Index build ms | WAL MiB |
|---|---|---:|---:|---:|---:|---:|---:|
| 8.0.30 | baseline / original | 122.5 | 44.3 / 713.1 | 447.9 | 0.51 | 664.2 | 18.93 |
| 8.0.30 | prhead / 64 MiB | 327.8 | 142.7 / 1071.0 | 145.0 | 10.50 | 627.5 | 20.45 |
| 8.0.30 | current / 8 MiB | 314.7 | 99.1 / 1052.2 | 70.4 | 2.74 | 576.2 | 20.45 |
| 8.0.30 | current / 64 MiB | 304.7 | 90.1 / 1223.5 | 99.5 | 2.15 | 639.5 | 20.45 |
| 8.0.30 | current / 256 MiB | 237.4 | 79.6 / 1143.5 | 75.2 | 2.20 | 546.3 | 20.45 |
| 10.0.9 | baseline / original | 260.7 | 45.2 / 707.0 | 537.3 | 0.48 | 789.3 | 18.93 |
| 10.0.9 | prhead / 64 MiB | 440.0 | 120.5 / 1131.8 | 141.6 | 24.32 | 820.2 | 20.45 |
| 10.0.9 | current / 8 MiB | 367.2 | 96.1 / 1196.6 | 57.0 | 6.77 | 540.4 | 20.45 |
| 10.0.9 | current / 64 MiB | 379.3 | 105.1 / 1284.0 | 62.0 | 8.46 | 630.7 | 20.45 |
| 10.0.9 | current / 256 MiB | 260.1 | 97.2 / 1279.7 | 85.2 | 6.36 | 604.8 | 20.45 |

Overflow release uses a separate 8 MiB cache with an 8,192-page transaction
threshold and reads 40,000 documents before rollback. The corrected engine
asserts actual segment release, allocation within the rounded target, and zero
readable pins after completion. The baseline retains its original policy, so its
lower release latency does not include returning those cache segments.

### 900,000-document index build

| Runtime | Implementation | Warm scan ms | Index build ms | WAL MiB | Retained cache MiB after indexed scan |
|---|---|---:|---:|---:|---:|
| 8.0.30 | baseline | 1990.4 | 3809.8 | 85.06 | 786.42 |
| 8.0.30 | current | 922.2 | 2987.5 | 92.05 | 64.06 |
| 10.0.9 | baseline | 1633.6 | 3501.8 | 85.06 | 786.42 |
| 10.0.9 | current | 830.8 | 2856.9 | 92.05 | 64.06 |

The JSON also records first scans, 1/4-thread lookup percentiles, CLOCK work per
lookup and per miss, bulk insert, encrypted update, vector search, and full cache
accounting. Baseline CLOCK counters are unavailable, not measured zero work.

## Decisions and remaining tradeoffs

Keep the 64 MiB file default and 1,000-page cooperative threshold: retained cache
after the large workload fell from about 786 MiB to 64 MiB, while the measured
index build remained faster. The WAL increased from 85.06 to 92.05 MiB (about
8.2%); that is a real cost of the fixed threshold, not hidden by checkpointing.
Memory-backed storage retains the 8 MiB default; its durable in-memory data is
separate from the cache target. These remain configurable starting points, not
optimal values for every workload.

Compared with the original PR head at 64 MiB, the correction reduced overflow
release p99 from 10.50 to 2.15 ms on .NET 8 and from 24.32 to 8.46 ms on .NET 10.
Normal release also improved in these samples. High-concurrency lookup p99 did
not improve consistently and remains worse than the pre-feature baseline. Warm
scans that fit in the old large cache can likewise be slower at 64 MiB. The
256 MiB profile shows why applications with larger working sets may choose a
larger target. No claim of throughput parity or elimination of all contention is made.

A lock-free page cache would need an additional lifetime/reclamation design;
removing the frame monitor or replacing it with a spinlock is not a safe
mechanical change. It remains to prevent eviction while a caller pins or copies
a frame. The existing deterministic race tests continue to cover that protocol.

An intermittent HNSW result-count issue was isolated independently on the
pre-feature baseline: in a 1,000-vector corpus, query 9 of a fresh graph returned
3 results for k=20. The runner records minimum result count and short-result
queries rather than treating timings as a recall guarantee. The final recorded
matrix had no short-result queries. This pre-existing vector-search behavior is
not repaired by the memory-lifecycle correction; vector persistence and content
checks still run.

## Verification

The later ownership audit and current verification results are recorded in
[`memory-lifecycle-audit.md`](memory-lifecycle-audit.md). The measurements and
test counts below describe revision 6 before those additional corrections.

The full solution Release build succeeds for all configured targets. Both .NET 8
and .NET 10 pass 433 tests with 7 skipped. The 86-test focused concurrency/memory
subset passed five consecutive runs. CI also compiles the measurement runner and
checks changed C# file sizes when a PR is opened. These corrections are pushed
separately on `bug/memory-leaks-followup`; PR #2772's earlier CI results do not
verify the follow-up branch.
