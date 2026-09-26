# Shared-mode performance after #3013

PR #3014 is stacked on #3013 at
`d2fb099acebb58dbbb07acbeea49c05bb025defd`. The selected production change is the
bounded reader-slot free list. Its library diff against the parent is confined to
`SharedReaderSlots.cs`; query admission, writer ownership, WAL recovery,
checkpointing and durability barriers use the parent's implementation.

All concurrent Shared participants are assumed to use exactly the same LiteDB
version. Mixed-version concurrency was not used to reject any experiment.
Persisted compatibility and database safety remain requirements.

## Selection

IDs below follow the original task; the feedback reordered their letters.

| Strategy | Decision |
| --- | --- |
| A: mapped reader admission | Implemented and tested, then excluded because repeated writer-throughput/tail regressions fail the task's strict performance gate. Both reader admission and writer publication were removed together. |
| B: incremental writer reopen | Implemented and tested, then excluded: no repeatable contended-throughput gain, extra allocation and write/checkpoint regressions. |
| C: reusable pin holder | No new protocol. Measured thread lifecycle cost is small relative to the existing pin quantum; the parent already reuses its mutex-owner worker for one idle second. |
| D: yielding | Keep the parent's adaptive policy. Fixed 10 ms yielding lost 11–38% aggregate writer throughput. |
| E: reader-slot free list | Selected. Constant-time slot selection preserves publication, failed-release conservatism, bounded capacity and all-generation protection. |

The mapped implementation and all its tests are preserved on
[`codex/shared-mapped-admission-candidate`](https://github.com/litedb-org/LiteDB/tree/130b339523cbe61efcef52b5d67252aee5a6cb53),
with [measured results](shared-mapped-admission-results.md) and a
[protocol note](shared-coordination-prototype.md). It reduces point mean latency
79–84% and mixed mean latency 44–52%, but saturated readers reproducibly lower
writer throughput, including −17.1% for Linux .NET 10 point reads and −24.1% for
Windows .NET 10 large scans. Equal offered load reduces, but does not eliminate,
the costs. Version-only and full-storage-stable cache-eligibility follow-ups also
fail to establish a regression-free replacement. Those gains are **not** claims
about the final free-list-only library.

The incremental writer prototype is preserved at `c5d379607`. Its native campaign
exposed a real checksum failure after partial checkpoint, metadata resume and an
uncommitted safepoint: reconstructing free slots from a smaller live query index
lost allocator witnesses needed by full recovery. Preserving the captured allocator
behind the complete prefix fence fixes the plaintext/encrypted reproductions;
12 append/checkpoint-death cases also pass. Its performance rejection remains.
No incremental writer metadata cache ships.

## Selected change and resource cost

`List<int>` stores each free slot's successor and `_free` identifies the head.
Only successfully cleared slots are linked back. Failed publication leaves its
entry retryable; failed release quarantines the entry rather than authorizing
reuse. An unpublished appended entry is unlinked before rollback removes its
storage. The existing database mutex, OS-backed liveness, conservative handling
of malformed/torn content and protection of every live version remain intact.

The free list adds at most 192 KiB of payload at the 65,536-slot cap relative to
`List<bool>`, retaining historical capacity until registry collection. Replacement
allocation remains 32 bytes. There is no new mapped page, retained engine, idle
lease, timer or background thread in the selected implementation. Slot/lease and
database/WAL formats do not change, and no database rebuild is required.

## Measurement evidence

Production comparisons use Release with `TestingEnabled=false`, isolated library
builds, identical runners, fresh processes, ten-second warmup and alternating
baseline/candidate order. Raw artifacts record commits, assembly hashes, command,
runtime/OS, chronological windows, complete result validation and final close/WAL
state. Host/runtime results are not pooled. Paired-bootstrap intervals are
exploratory per-metric estimates; intervals crossing zero are inconclusive, not
proof of equivalence.

The initial free-list-only Linux .NET 10 comparison (`b33c00733`) reduced median
helper latency at capacity from 3.051 to 2.099 microseconds; mean paired change
−33.0% [−35.7, −30.9]. At 4,096 slots the interval crossed zero. Ordinary point,
scan and mixed workload intervals also crossed zero, so this is a helper
improvement, not an application-throughput claim. The final library source is
identical to the earlier free-list-only comparator `6d71e39bb` and is revalidated
against the current parent after removal of the mapped candidate.

The production workflow covers slot fanout, point and complete 2,000-row scans,
read/write mixtures, write-heavy/transactional work, connection churn, checkpoints,
two/four contending writers and lifecycle accounting. Separate reader comparisons
cover point/medium/large scans, one/four readers, continuous commits and checkpoints.
The PR links the final-head runs and their reconciled measurements. A successful
benchmark process means its data checks passed; performance acceptance is assessed
from the paired results separately.

## Safety validation and reproduction

The focused free-list tests cover irregular churn, off-thread/double release,
all 65,536 slots, failed republication, and failures after zero, partial or complete
release writes. Existing registration/header failure, live-generation, process-death
and full-document/index tests remain. The deliberately unsafe free-list control
fails by reusing a slot whose release outcome is unknown. The invariant is tested
independently through published versions and file-backed fault injection.

The final branch must pass the actual Framework 4.6.2/4.8.1 hosts, modern
.NET 8/9/10 Windows x86/x64, Linux x64/ARM64 and macOS jobs, native Windows
concurrency, fuzz and migration workflows. Reconciliation records all discovered
partitions, actual runtime/architecture gates and existing skip reasons. Finite
campaigns supplement these tests; they do not establish arbitrary device or
power-failure behavior.

```sh
python3 scripts/run-shared-next-campaign.py \
  --runner LiteDB.Fuzz/bin/Release/net8.0/LiteDB.Fuzz.dll \
  --scratch /absolute/private-temp-on-test-volume \
  --output artifacts_temp/shared-next-smoke --smoke
python3 scripts/run-shared-next-campaign.py \
  --runner LiteDB.Fuzz/bin/Release/net8.0/LiteDB.Fuzz.dll \
  --scratch /absolute/private-temp-on-test-volume \
  --output artifacts_temp/shared-next-extended
```

Build fuzz/tests with `TestingEnabled=true` in an isolated checkout. Smoke runs
seeds 3012–3014 with four steps; extended runs seeds 3012–3027 with 64 steps for
snapshot/shared/MVCC-retirement/index. Each target/seed has its own artifact
partition and replays built-in regressions. Retained discovered inputs can be
replayed individually; they are not recursively replayed by every later seed.
Local work uses one heavy job at a time, a private ext4 TMPDIR, sessions below
300 seconds, a 256 MiB runner/512 MiB externally accounted campaign cap and a
10 GB total task disk budget. Failures are preserved before recovery or retry.

[Published investigation and candidate evidence](https://github.com/litedb-org/LiteDB-Artifacts/tree/78023619526848e6d93696b12cea81c0f6705ebd/pull-requests/3014-shared-performance)
contains successful, incomplete, superseded and negative-control runs with explicit
classifications. Its mapped-candidate qualification archive is not final selected
source evidence. All 32 original synthetic failure images retain their original
hashes. The final selected-source evidence is published separately and linked from
the PR; excluded experiments are not counted as final-source test passes.
