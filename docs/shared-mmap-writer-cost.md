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

Writable opens still conservatively bracket recovery. Filesystem revocation and
lease publication are preserved. Narrowing open scopes or replacing leases would
require additional evidence covering all recovery mutations and fallback writers.

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
