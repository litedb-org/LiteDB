# Shared mmap writer-cost investigation

This follow-up restores the archived mapped admission implementation from
`130b339523cbe61efcef52b5d67252aee5a6cb53` on the merged #3013/#3014 Shared stack
(`c12b6a72149ad2166d62769d07efe961198b7a38`). The restored library is byte-for-byte
the archived source before the separately recorded optimizations. `dev` did not
contain that stack when this work started.

Qualification is in progress. Neither historical safety results nor an individual
benchmark constitutes acceptance of the revised implementation. The initial
performance target is no repeatable writer throughput/p99 regression greater than
5% relative to the merged implementation, while retaining the archived reader gains.

## Protocol changes under evaluation

1. Aligned acquire status loads on x64/ARM64; x86 retains compare-exchange reads.
   Seqlock validation and post-lease admission retain full memory barriers.
2. End a protected writable open and establish local trust in one publication.
3. Publish a monotonic append commit through one atomic version update. Equal
   versions need no write. Resets and interrupted sequences retain full sequencing.
4. Publish WAL reuse before the first overwrite of each write batch. Only an
   `IBatchedCoordinationSignals` participant can coalesce: its protocol excludes
   cold snapshot installation for the whole batch. The separate coordinator keeps
   per-frame notification. Each safepoint or new batch announces independently,
   including failure cleanup; no durability barrier or allocation rule changes.
5. Update the last-use timestamp on reader completion. The existing timer checks
   activity and rearms itself; streaming readers retain leases, idle caches do not.

6. Treat the first status lookup as a hint, omitting its two revocation probes.
   The final authoritative lookup after lease publication still performs both
   filesystem probes and the full fence; no query runs on the hint alone.
7. Experimentally fence actual startup mutations instead of every writable open.
   Initialization, header repair, checksum conversion, tail truncation and v7
   upgrade now have explicit structural scopes. Automatic rebuild fences before
   scanning external leases. Existing checkpoint/format-promotion/reuse scopes
   remain. An interrupted mapped publisher still requires a broad protected open.
   Additional recovery qualification is in progress; this is not accepted yet.

## Evidence location and reproducibility

Local raw results, build logs, patches, and TRX reports are under
`artifacts_temp/mmap-writer/`. Production variants use isolated worktrees and
Release / `TestingEnabled=false`; tests use the main worktree with hooks enabled.
No timing run overlaps this task's builds or tests. Other host activity is outside
this task's control. The host is Linux x64, Ryzen 9 3900X, with database scratch on
the original `/tmp` ext4 volume. Every runner validates full payloads, generations,
secondary-index results and final WAL cleanup; measured throughput does not by
itself establish correctness or acceptance.

The reader driver also supports a finite `--reader-interval-ms` to separate equal
offered load from saturation. Writers remain unthrottled. API and intended-arrival
latencies, offered/completed work, allocations, CPU and lifecycle costs are retained.

## Safety coverage

The restored tests cover native admission/checkpoint interleavings, killed readers
and writers, partial leases, fallback revocation, unknown files, encrypted reads,
resource disposal and idle lease release. New tests distinguish append publication
from resets/interrupted publishers, per-batch notification from per-frame protocol
requirements, failed overwrite cleanup, and timer visits during streaming reads.
The broader Shared/MVCC/recovery suites and finite campaigns remain required.
Process death does not simulate loss of the OS cache; persistence-fault tests cover
their own explicit lost/torn-write and failed-flush models. Cross-platform results
must name their actual tested runtime and architecture.

## Initial Linux .NET 10 results

Five alternating pairs, 10-second warmup and 10-second measured interval,
4 reader processes and one unthrottled writer (200-document durable transaction).
Values are medians; paired changes use an exploratory deterministic bootstrap.

| Comparison / readers | Writer transactions/s | Writer p99 ms | Reader calls/s |
| --- | --- | --- | --- |
| Archived → acquire loads, point | 23.48 → 23.72 | 50.89 → 50.23 | 829 → 940 |
| Merged → combined, point | 23.20 → 23.49 | 52.76 → 50.41 | 217 → 929 |
| Merged → combined, large scan | 22.58 → 22.59 | 53.11 → 53.69 | 90.20 → 94.10 |

Combined is library `ad7de069d`: loads, duplicate removal, monotonic publication,
reuse batching and timer changes. In the large-scan comparison writer throughput
changes −0.24% [−0.68, +0.08], while writer p99 changes +3.52% [−2.17, +8.28].
The latter is inconclusive, not proof of a 5% equivalence bound. Point readers use
15.7% more aggregate process CPU while completing 4.15 times as many reads.

These local runs do not reproduce the archived hosted regression magnitude.
The original Linux .NET 10 hosted point results have 68.44 → 56.76 writer
transactions/s and 379 → 12,181 reader calls/s; Windows large scans have
44.02 → 33.92 writer transactions/s and 163 → 616 reader calls/s. Their raw
reports were inspected. The local host's lower reader concurrency and different
I/O timings make hosted qualification necessary before claiming the writer problem
is solved. Hardware performance counters are unavailable (`perf_event_paranoid=4`);
no system permissions were changed to collect them.

## Isolated coordination measurements

A temporary reflection-bound helper runner exercises production methods directly;
delegates are bound before timing. Five alternating rounds per variant, a two-second
warmup and one million operations per process validate the final status/version.
These are helper costs, not database transaction latency.

| Cumulative variant | Commit publication ns | Writable-open publication ns | Status read ns |
| --- | ---: | ---: | ---: |
| Archived | 31.82 | 104.26 | 2146.55 |
| Acquire loads | 23.27 | 76.59 | 2112.96 |
| Remove duplicate open publication | 22.59 | 63.21 | 2126.31 |
| Atomic monotonic commit | 14.46 | 63.49 | 2139.22 |
| Batch reuse | 14.53 | 63.66 | 2130.28 |
| Timer change | 14.69 | 64.21 | 2110.50 |

The final two changes are not exercised by this helper; their unchanged values
are controls. Batching's event counts/failure boundaries are tested separately.
The full status call retains its filesystem revocation checks. Nanosecond savings
in publication cannot alone explain a double-digit end-to-end writer regression.
Raw inputs/source and per-run library hashes are in `coordination-costs.jsonl`
and the sibling `coordination-probe` directory.

Local Shared/coordinator/WAL selection on .NET 10 passed 461 cases, including the
new publication/reset, batch/failure and timer tests. Hosted CI and reader
comparisons are running on `b2010e8bd`; their completion remains outstanding.

Local .NET 8 also passed the same 461-case selection. Optimized x64 disassembly
shows eight `lock cmpxchg` instructions per archived status scan and none in the
acquire-load scan; its explicit full fence remains. Both disassemblies and the
runtime memory-model source consulted are retained with the local evidence.

| Changed invariant | Discriminating tests |
| --- | --- |
| Append publication preserves old storage while exposing the new version | `SharedCoordinationPublication_Tests.Append_commit_changes_only_the_visible_version`; existing native concurrent document/index oracles |
| Reset or interrupted publication invalidates old cached state | `Version_reset_still_invalidates_old_snapshots`, `Interrupted_sequence_requires_recovery_even_for_an_unchanged_version`; mapped process-death tests |
| Each destructive batch announces before mutation, including failed writes | `SharedWalBatchPublication_Tests` (batched/unbatched and success/failure); `SharedWalReusePublication_Tests`; `WalSlotReuse_Tests` partial-write rollback/recovery |
| Idle expiry never drops an active lease or strands cleanup | `SharedCachedIdleTimer_Tests`; restored idle, spill, finalization and native reader-death tests |
| Admission still protects every accepted generation | existing forced final-recheck negative control; Shared generation/reclamation tests; bounded snapshot/shared/MVCC-retirement campaign |

The local selections used Release with `TestingEnabled=true`; production source
is `ad7de069d` and new tests are in `e417f5884`. Later commits through `b2010e8bd`
change benchmark support and documentation, not library behavior. The campaign and
hosted matrices have not yet been recorded as completed qualification.

The bounded local campaign passed 12 invocations (seeds 3012–3014, 32 steps each
for snapshot/shared/MVCC-retirement/index): 384 primary steps plus six built-in
replays. It recorded 288 held-generation validations, 672 between-generation
checkpoints, 288 Shared child processes, 3,282 acknowledged rows, 78 crash-position
observations (42 internal), and 96 Shared integrity checks. Counts are summed
observations, not distinct faults. Each Shared seed reached all 26 configured
crash positions, including 14 internal boundaries. All independent payload/index
and integrity checks passed. Maximum invocation was 54.3 seconds, with about
6.2 MB externally accounted artifacts. The campaign ran against unchanged library
source; concurrent working-tree edits were documentation-only and their patch plus
binary/source-tree hashes are retained. This is a three-seed follow-up, not a claim
to have rerun the archived 16-seed campaign.

## Hosted rejection of the first combined variant

Run `36308282410` measured production `b2010e8bd` against the merged baseline.
Linux .NET 8, four saturated point readers: writer throughput changed −14.18%
[−15.37, −13.19] and p99 +11.26% [+8.01, +14.46], despite reader throughput
increasing about 20 times. Windows .NET 10 checkpoint workloads also regressed:
point throughput −28.19%, large −16.91%, with substantially worse writer p99.
These results reject the first combined variant against the provisional target.
The publication helper savings do not solve the end-to-end problem.

Idle Linux .NET 10 comparison against the archived mapped implementation retained
reader performance: point +1.90% [+0.86, +2.78], large +0.13% [−1.29, +1.89].
Equal offered load locally completes approximately 80 large reads/s with writer
throughput −0.93% [−1.62, −0.23], but that does not excuse saturated regressions.

The initial full CI exposed a Windows sharing-mode error in the new publication
test helper; `f69ba57f4` fixes the helper using an explicitly shared stream.
The old run and a duplicate queued run were stopped while implementation continues;
their partial results are not final qualification. The five corrected tests pass
locally. Windows confirmation remains required.

## Rejected mapped reader-lease experiment

Archived revision `b02672b75` (library `f8b8f9d18`) added a fixed 4 KB table containing 511 atomic version/complement slots.
Only cached coordinated reads use it; scoped fallback keeps its file-backed table.
One connection may therefore own one table of each kind. The new `mapped-` lease
prefix makes older parsers fail closed. Scanners and publishers both map the table;
the implementation does not assume coherence between file reads and mapped writes.
An exclusive OS lease handle still proves process liveness. No table is resized
while published, and exhaustion falls back through ordinary query admission.

The table is created under database ownership, and hot admission can only reuse
an established table. Idle caches release their slots immediately. Scanner views
are currently opened for each scan; their added open/map costs are part of the
experiment. It was reverted from the working candidate after measurement; the
branch `codex/shared-mmap-leases-experiment` preserves it locally.

New tests exercise full tables, malformed contents and lengths, version zero,
mixed lease formats, deferred disposal and prohibition of unowned table creation.
Existing generation tests now explicitly exercise the bounded two-table case;
file-write injection tests explicitly select the fallback protocol. A partial
file-backed publication is still tested with live mapped peers. Native mapped
death tests cover atomic publication before final admission. Production phase
timing records writer begin/update/commit costs separately to identify where any
end-to-end regression occurs. Hosted run `36310590505` found Linux point readers
+11.89% [+9.40, +14.33], but writers −1.05% [−1.61, −0.44] against the already
regressed combined variant. Windows writer comparisons were noisy and did not
establish recovery of the lost throughput. Extra table/mapping complexity was not
accepted without solving the writer problem. A local broad selection with the mapped
prototype and subsequent cache-retirement change passed 474 cases; that is not a
qualification of either standalone variant.

## Ownership delay and cache retirement

Production phase timing in run `36310099049` compares the merged baseline with
the first combined variant. Linux .NET 10 point workloads have median writer
begin time 1.40 → 3.53 ms/transaction, while updates remain 4.99 → 5.09 ms and
commit 6.75 → 6.75 ms. Windows point begins grow 1.10 → 3.19 ms; large begins
3.10 → 4.99 ms, with large commits 8.43 → 8.41 ms. Begin includes ownership
waiting and writable-engine opening; it does not by itself separate those costs.

The candidate at `46963eb9d` retired an invalid cached snapshot before waiting for database
ownership. Previously its accumulated pages were released while installing the
replacement under that mutex. Active streaming readers still retain their engine
and lease until their own disposal. A targeted interleaving checks retirement
while another connection holds a write transaction, then verifies both the new
query's committed value and every older streaming value. Its 463-case local
Shared/coordinator/WAL selection passed on .NET 10, but hosted run `36311367955`
did not recover writer performance: Linux point writer throughput fell another
2.02% [−3.14, −1.23] against the first combined variant. It was removed from the
working candidate and retained on `codex/shared-cache-retirement-experiment`.

An explicitly instrumented diagnostic run (`36311407545`) separates engine
opening from ownership waiting. Linux writer open was 0.088 → 0.119 ms/call,
but ownership wait 1.18 → 3.90 ms; Windows open 0.196 → 0.277 ms and wait
1.29 → 5.20 ms. Reader cache retirement averaged about 0.01 ms/call. These
instrumented builds are not production performance evidence. Their exact source
patch is retained in the workflow artifacts, and normal runners report no
ownership profile.

The expanded diagnostic (`36311862394`) measured native waiting separately.
Linux writer wait grew 1.03 → 3.64 ms/call: native wait 0.99 → 2.61 ms and
holder handoff 0.043 → 1.06 ms. Windows native wait grew 1.25 → 2.45 ms and
handoff 0.049 → 0.52 ms. Cold reader open/query work remained sub-millisecond.
The production spin-count experiment (`36311998025`, variant `9d85a8702`)
found no consistent writer recovery. Linux had variable durable-flush times;
Windows point writer change was −0.96% [−4.08, +2.36]. It was not adopted.

## Reduced hint probes and rejected continuous writer pressure

Revision `640aaa1c4` removes only the provisional hint's two filesystem probes.
The strengthened fallback tests prove a lease can still be published after
revocation, while the final authoritative check rejects it and reads the new
committed data through ownership. The broad 461-case .NET 10 selection passed.
Hosted run `36312799557` compared it with the first combined variant: median idle
point throughput rose 226,570 → 273,527 calls/s on Linux and 51,806 → 66,053 on
Windows. Active-writer point reads rose 12,538 → 13,425 and 3,548 → 4,181;
writer medians were approximately unchanged. This alone does not close the
writer gap against the merged implementation.

Revision `cfca6d460` added an expiring 100 ms scheduling hint before requesting
writer ownership. Accepted cached queries yielded once while the hint was live.
It changed no leases, durability or admission authority. Hosted run `36313054364`
compared it directly with `640aaa1c4` using five alternating validated pairs:

| Host / workload, four readers | Paired writer throughput change | Reader throughput change |
| --- | ---: | ---: |
| Linux point / writer | +17.13% [+16.15, +17.97] | −82.34% |
| Linux point / checkpoint | +16.80% [+15.30, +18.28] | −77.11% |
| Windows point / writer | +7.30% [+1.68, +14.08] | −55.74% |
| Windows large / writer | +4.54% [−1.55, +11.79] | −39.96% |

That reader cost is too high. The hint was reverted; the local branch
`codex/shared-pressure-experiment` preserves the tested revision. All four jobs
validated their data. Locally the same point experiment mainly reduced readers
(−9.56%) while writers changed +0.24%; slow local durable flushes again limit
how representative those measurements are.

## Narrowing startup invalidation

Revision `323b6c31e` moves structural fencing to actual startup mutations.
Four deterministic plaintext/encrypted cases prove that a cached point reader
finishes while a real writable transaction holds database ownership, and verify
commit/rollback payloads, indexed results and cold reopen. Two publication cases
cover interrupted seqlock/structural publishers. Native tests now distinguish
an untouched writer open from a child killed inside actual tail repair.
File-backed mutation guards cover clean open, initialization, legacy conversion,
data/WAL tails, and repeated torn header repair with preserved redo. Rebuild
coverage checks that its fence precedes the external lease scan.

The first hosted dispatch (`36314066290`) failed before building because checkout
requires a full SHA rather than the abbreviated baseline provided. It contains
no performance results. Corrected run `36314228934` compares the same candidate
with the full `640aaa1c43df36a95a83aefbd853561c99b79bec` baseline.
