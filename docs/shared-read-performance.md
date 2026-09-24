# PR #3003: shared read performance investigation

This investigates the remaining read gaps in
[PR #3003](https://github.com/litedb-org/LiteDB/pull/3003), at `f0228d301`,
against pre-storage-stack `0fd277aae`. The original Windows results and harness
are linked from the PR. These new measurements are Linux results, not a
reproduction of the Windows percentages.

## Findings

1. **Repeated collation fingerprints are a confirmed avoidable cost.** Every
   calculation writes 361 culture comparisons and hashes the result. A clean
   checksummed engine open checks the stamp before WAL restoration, inside
   restoration, and afterward. Shared mode repeats that work for every operation;
   large reads open a second engine as well. This computation arrived with the
   persisted index compatibility changes, so attributing all regression to page
   checksums misses a significant cost.
2. **Shared mode still rebuilds state on every operation.** Engine construction,
   file opens, header parsing, page-cache allocation, and query setup remain in
   the profile. The PR's persistent handle cache is Windows-only. This Linux
   investigation cannot establish how much more Windows handle caching could save.
3. **Streaming queries do duplicate work.** `TryBufferResult` consumes a prefix
   of up to 100 values/64 KiB, then discards it and starts the query on a second,
   leased snapshot engine. The prefix is extra work even for a simple scan;
   a sort or aggregate can do substantially more input work before producing that
   prefix. The second engine begins with a fresh page cache. These are architectural
   costs; the fingerprint fix does not remove them.
4. **Lazy checkpointing exchanges write cost for subsequent replay work.** The
   shared connection can leave fewer than 50 WAL pages after an operation.
   Following reads restore that WAL again. In the mixed-workload trace, WAL index
   restoration appears in 15.1% of sampled main-thread time and native durable
   flushes in 49.5%. These inclusive percentages overlap with their callers and
   include waits. Faster fingerprinting cannot remove storage latency.
5. **Holder-thread handoff and missing-marker probes are smaller remaining
   costs.** The point trace attributes about 3.3% to mutex admission and 4.7% to
   the rebuild recovery marker check. Neither safety mechanism was bypassed.
   Inlined CRC work is not reliably isolated by these samples; a small named CRC
   stack percentage is not evidence that checksums are free.
6. **The coordinator cursor-update gap is dominated by waiting at the client.**
   A separate-process, 2,000-update run succeeds. An attached 10-second trace
   places 99.6% of sampled client main-thread time in protocol reads. The streaming
   write protocol requests the first document, requests the next/end marker, then
   sends the result, even for a single-document update. The shared engine instead
   pins its engine during same-connection cursor writes. Fewer protocol exchanges
   are a plausible next optimization, but this client trace includes server work
   and durable commit latency inside the wait; it does not prove that transport
   alone explains the gap. The coordinator is unchanged by this patch.

The baseline point trace contains 6.06 seconds of sampled main-thread time;
`CollationFingerprint.Compute` appears in 26.2%. The candidate trace contains
4.99 seconds, with cached computation at 0.6% and uncached computation at 0.2%.
These whole-process traces include fixture creation and startup and are diagnostic
evidence, not the uninstrumented benchmark speedup. The scan trace puts only 3.7%
in fingerprinting; its larger iteration costs remain.

## Change and safety boundary

`CollationFingerprint` now keeps at most 64 immutable runtime results in a
process-local table. Keys include comparer name, LCID, comparison options, and
sort version/ID. Independent `Collation` instances can reuse the result; comparing
`CompareInfo` object identity alone did not achieve that across engine opens.
Collisions recompute. Publication uses volatile reference reads/writes and retains
no database, page, stream or connection object.

The uncached algorithm, persisted fingerprint bytes, header comparisons, and
legacy zero-stamp validation remain unchanged. A runtime without sort-version
support still returns zero and retries the runtime on the next request. Each
process computes its own probes, so a cache cannot carry an answer from ICU to
NLS or another runtime. Every newly loaded data/WAL header is still compared
against the runtime result. The change adds no persistence transition or format.

This does not enable incremental shared-engine reopen. The fence considered in
[#3004](https://github.com/litedb-org/LiteDB/issues/3004) misses foreign writes into
reusable WAL slots without a header change. Removing that replay safely needs a
separate coherence protocol. The experimental coordinator has such an epoch/lease
protocol but also changes deployment and ownership; it remains opt-in.

## Measurement protocol

Ubuntu 24.04.3, AMD Ryzen 9 3900X, x64, ext4 on the host's LVM volume; .NET
10.0.11 and 8.0.30 measured separately. All library builds use Release and
`TestingEnabled=false` in separate worktrees. Other database/fuzz workloads were
active on the host. Treat latency ranges as host-dependent rather than isolated
hardware limits. No tests or builds from this investigation run during the final
timing comparison.

The [runner](../tools/SharedReadBenchmarks/README.md) creates 2,000 documents with
a 200-character payload under invariant current culture, checks their full contents, separates a first operation
from warm measurements, and records the loaded DLL's SHA-256. Three interleaved
rounds reverse build order in the middle round. Each process has fresh static
state and a freshly seeded database. Point reads use 6,000 measured operations,
scans 150, and mixed workloads 2,000. Warmup is 1,000 operations or 20 scans.

Raw local evidence is retained under `artifacts_temp/pr3003/`: `comparison.jsonl`,
the `.nettrace` and `.speedscope.json` files, `profile-summary.txt`, production
assemblies, and test logs/results. Raw artifacts are not included in the source
diff. The original PR harness was also run before adding the stronger content
checks; those exploratory timings are separate from the final comparison.
`coordinator-attached.nettrace` is the valid coordinator trace. The earlier
launch-traced attempt exited 134 and produced unusable stacks; it is excluded
from the analysis. Both an uninstrumented run and the subsequent attach-traced
run completed successfully. Coordinator diagnostic runs overlapped test work
and are not used for latency comparisons.

## Results

Median of three run means, milliseconds per operation. Ranges are the minimum
and maximum run means, not per-operation extremes.

| Runtime / shared workload | PR head | Candidate | Change |
| --- | --- | --- | --- |
| .NET 10 point | 0.4402 (0.4233–0.4450) | 0.3309 (0.3206–0.3383) | −24.8% |
| .NET 10 scan | 11.1429 (10.5981–11.1618) | 10.1198 (9.9882–10.2407) | −9.2% |
| .NET 10 mixed | 1.3015 (1.2717–1.3020) | 1.2527 (1.1744–1.2547) | −3.8% |
| .NET 8 point | 0.4529 (0.4525–0.4796) | 0.3542 (0.3476–0.3556) | −21.8% |
| .NET 8 scan | 10.5443 (10.4339–11.3632) | 10.1768 (9.9774–11.0096) | −3.5% |
| .NET 8 mixed | 1.3689 (1.3687–1.3732) | 1.2052 (1.1794–1.2123) | −12.0% |

The point-read improvement is supported by both disjoint timing ranges and the
removed profile hotspot. Scan results need more caution: the .NET 8 ranges
overlap and its median p99 increases from 13.10 to 15.29 ms. The .NET 10 direct
scan control also moves from 5.73 to 5.26 ms with overlapping ranges, despite
unchanged allocation and no per-scan fingerprint work. Thus the entire observed
9.2% shared-scan improvement cannot confidently be attributed to this patch.
Direct point reads and mixed work remain essentially unchanged (0.0616→0.0627
and 0.5785→0.5763 ms); their allocation counts are identical.

Shared point allocation falls from about 289.5 KB to 280.5 KB per operation;
scan allocation from 5.345 MB to 5.327 MB. These are total allocated bytes,
not resident or retained memory. The new table retains at most 64 small entries
in exchange for removing those repeated temporary allocations.

The .NET 10 phase diagnostic measures obtaining a scan reader at 1.206→0.941 ms,
iteration at 9.017→8.571 ms, and disposal at 0.031→0.030 ms. The unchanged
iteration implementation and the direct control indicate environmental noise in
part of the iteration difference. Reader setup is still much higher than the
pre-stack value of 0.123 ms.

The pre-stack shared point/scan/mixed values on this host are 0.2127 / 8.3444 /
1.3133 ms. The candidate remains about 56% slower for point reads and 21% slower
for scans, while mixed work is slightly faster. Those are Linux comparisons;
the PR's reported Windows +35% / +60% / +65% gaps are not interchangeable with
them. The remaining gap is not closed.

Production library identities:

| Build | SHA-256 |
| --- | --- |
| PR head, net10.0 | `832b3d4b9ea4dd90430d882651a30350f799382386ad6156eb35614f7d5a451d` |
| Candidate, net10.0 | `bb15a99e96e389f3f66319a16667633c92adb50af1d57cc78c492fe3432db98a` |
| PR head, net8.0 | `ad51259daac2c1115f83cdd9b09588ebcc251862669445a4f2ffe7054d94ebae` |
| Candidate, net8.0 | `c2625f080828846f543c65f99386677d5c17f75532fbee33867ddc039cf7884f` |

## Validation and limits

The library revision tested is `f0228d301` plus the fingerprint cache in this
change, Release with `TestingEnabled=true`. The production measurement worktrees
use the identical cache source with hooks disabled.

| Invariant / risk | Evidence |
| --- | --- |
| Cache collisions or concurrency must not alter persisted fingerprints | 108 culture/options combinations, more than the 64 slots, repeatedly checked concurrently against the original uncached algorithm; fixed pre-cache Ordinal value; invalid-option rejection |
| A warm connection must still reject an incompatible header | File-backed writable/read-only shared opens, a checksum-valid altered stamp after a successful read, two rejected retries, unchanged data and absent WAL, then successful reads after restoring the original bytes, including an unrelated collection |
| Cache reuse must preserve indexed results and encrypted/read-only access | Plain/encrypted file reopen cases, explicit index-plan assertions, results compared with native culture comparisons, and byte-identical files after read-only use |
| Recovery and unversioned stamps must still be validated | Existing `Issue2812CollationStamp_Tests`, including a changed recovered WAL header, incompatible unstamped indexes, and valid zero-stamp read-only files |
| Actual old-writer compatibility | `scripts/test-index-compatibility.py` passed creation, migration, old-reader rejection and verification with real 5.0.21 binaries, 16 plain/encrypted, binary/culture, data/WAL, success/collision fixtures plus finite-limit fixtures |
| Broader query, ownership and storage safety | All nine CI partitions passed on both .NET 8.0.30 and 10.0.11: **4,471 passed, zero failed, seven existing skips per runtime**, excluding repeated runtime/hook guards |

Partition verification placed all 2,105 discovered methods in exactly one group.
The initial unpartitioned .NET 10 session reached the configured 300-second limit
with 3,093 passes and no failures; it was not counted as a completed suite.
Partitioned runs retain that same timeout and verify the actual runtime,
architecture, and presence of test hooks. The final index-plan assertion was
additionally checked with the focused fingerprint/stamp tests on both runtimes.

The .NET Framework 4.6.2 and 4.8.1 test targets compile; their runtimes were not
executed. Windows/NLS and macOS performance were not measured. The existing
fault-injection and process-recovery tests passed on Linux, but this optimization
does not claim new hardware power-loss guarantees. No persistence protocol,
format, checksum, checkpoint fence or ownership rule was relaxed.
