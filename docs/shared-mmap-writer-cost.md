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
