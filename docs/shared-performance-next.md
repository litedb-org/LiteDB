# Shared-mode performance after #3013

PR #3014 is stacked on #3013 at
`d2fb099acebb58dbbb07acbeea49c05bb025defd`. The current production/test source is
`2f7a2350c8476e6807e5c2e3e5f09ea5b46d542b`.
**Safety validation is complete for this source; performance acceptance remains
blocked.** The 99 focused tests, hosted platform matrix, finite campaign and
production comparisons pass their correctness checks. Mapped admission has
repeatable writer-throughput/tail costs described below; accepting those costs
requires a tradeoff decision under the task's no-regression requirement.

All concurrent Shared participants must use exactly the same LiteDB version.
Concurrent mixed-version, Direct/Shared and cross-machine access are unsupported.
This supersedes the original mixed-version objection to mapped admission and
incremental reopen. Persisted compatibility and durability remain requirements.
The [protocol note](shared-coordination-prototype.md) specifies eligibility,
publication ordering, lifetime, writer participation and fallback.

## Selected and rejected implementations

IDs below follow the original task; the feedback reordered their letters.

| Strategy | Decision |
| --- | --- |
| A: mapped reader admission | Selected candidate. Warm readers publish their actual snapshot version, fence and validate storage epochs without acquiring the database mutex. Cold opens remain protected. Performance/fairness qualification is still open. |
| B: incremental writer reopen | Implemented, measured, excluded. Five-pair comparisons found no repeatable contended-throughput gain, about 38 KB extra allocation per write, and reproducible write/checkpoint regressions. |
| C: reusable pin holder | Excluded. Measured thread start/join cost is small relative to the pin quantum; `SharedMutexOwner` already reuses its worker for one idle second. |
| D: yielding | Keep the parent's adaptive policy. The fixed 10 ms experiment lost 28–38% aggregate throughput with two writers and 11–28% with four. |
| E: reader-slot free list | Selected. Replaces linear selection with a bounded free list; publication, failed-release conservatism and all-generation protection remain intact. |

The excluded writer implementation is preserved on
`codex/shared-writer-reopen-prototype`; its
immutable implementation/fix revision is `c5d379607` and its
[production comparison](https://github.com/litedb-org/LiteDB/actions/runs/36269311392)
records the earlier measured source. Its native campaign exposed a real checksum
failure after partial checkpoint, metadata resume and an uncommitted safepoint.
Reconstructing free slots from the smaller live query index reclaimed intermediate
committed frames still needed by full recovery. The fix preserves the captured
allocator behind the complete prefix fence. Plaintext/encrypted reproductions
fail before and pass after; 12 append/checkpoint-death cases also pass. This safety
fix does not reverse the experiment's performance rejection. No writer metadata
cache or incremental WAL refresh is included in this PR.

A separate read-demand-only authority experiment (`f542313e6`) avoided creating a
page from pure writes. Its four-platform throughput intervals mostly crossed zero,
with opposite Windows .NET 8 signals for two versus four writers; ordinary traffic
also showed no consistent gain. It is excluded. The selected implementation instead
skips the first protected read's authority probe, removes publication delegates,
and closes ephemeral views without an explicit flush.

## Measurements and uncertainty

Production libraries use Release and `TestingEnabled=false`, in separate
checkouts. Runners reject test-hook assemblies. Head/base and binary hashes,
commands, OS/runtime, warmup, chronological windows and raw results accompany each
artifact. Unless stated otherwise, comparisons use five alternating pairs, fresh
processes, ten-second warmup and complete result/final-close validation. Percentages
are the mean paired relative change; brackets are deterministic paired-bootstrap
95% intervals. Host/runtime results are not pooled. Intervals crossing zero are
inconclusive, including when their median looks favorable.

### Ordinary reads and slot selection

[Production run 36276145919](https://github.com/litedb-org/LiteDB/actions/runs/36276145919)
compares `2f7a2350c` with parent `d2fb099ac`.

| Host/runtime | Point mean, median ms/op before → after | Point mean change | 2,000-row scan mean change |
| --- | --- | --- | --- |
| Linux .NET 8 | 0.07228 → 0.01268 | −82.3% [−83.4, −81.0] | −42.9% [−44.6, −40.9] |
| Linux .NET 10 | 0.07443 → 0.01211 | −83.7% [−84.4, −82.8] | −40.7% [−43.2, −38.2] |
| Windows .NET 8 | 0.09135 → 0.01931 | −78.9% [−79.5, −78.4] | −49.3% [−49.9, −48.7] |
| Windows .NET 10 | 0.20142 → 0.03616 | −81.9% [−82.1, −81.7] | −40.4% [−41.3, −39.4] |

Point p99 falls by 89–94%. At the 65,536-slot cap, helper mean latency changes
are −56.5%, −45.4%, −21.9% and −32.2% in the same platform order; replacement
allocation remains 32 bytes. This helper result is not an application-throughput
claim. Earlier free-list-only Linux/.NET 10 measurements (`b33c00733`) gave
3.051 → 2.099 µs at capacity, −33.0% [−35.7, −30.9]; ordinary workload intervals
crossed zero. The mapped-admission-only comparison against free-list `6d71e39bb`
independently showed 79–82% lower point mean latency
([run 36269004080](https://github.com/litedb-org/LiteDB/actions/runs/36269004080)).

The 3,000-call mixed workload had unstable p99 estimates. The predeclared
[20,000-call, ten-pair follow-up on final production source](https://github.com/litedb-org/LiteDB/actions/runs/36277289971)
uses `2f7a2350c` library code and parent baseline; measurement-only head
`667bfe3d4` changes count/rounds/workflow, not the library:

| Host/runtime | Mean change | p99 change | CPU/call change |
| --- | --- | --- | --- |
| Linux .NET 8 | −44.5% [−46.3, −42.4] | +10.4% [−3.9, +24.8] | −52.8% [−53.6, −52.1] |
| Linux .NET 10 | −44.8% [−45.7, −43.8] | +2.6% [−3.0, +7.7] | −54.1% [−54.8, −53.3] |
| Windows .NET 8 | −52.1% [−52.5, −51.5] | +1.1% [−1.5, +3.9] | −55.7% [−56.7, −54.4] |
| Windows .NET 10 | −52.3% [−53.3, −50.7] | +1.5% [−2.8, +7.1] | −56.3% [−57.4, −54.7] |

Mixed allocation falls about 63%. All four p99 intervals remain inconclusive;
these are exploratory per-metric intervals, not simultaneous or equivalence
bounds. Close latency is about 0.7–1.6 ms higher on several point/scan hosts,
and 0.9–1.0 ms higher in Windows mixed medians. Lifecycle CPU includes cleanup
and remains lower for these long read-heavy workloads. Extra close cost remains
a tradeoff after non-flushing ephemeral-view cleanup.

[Direct reference run 36276990204](https://github.com/litedb-org/LiteDB/actions/runs/36276990204)
uses identical `2f7a2350c` production library source for both modes. Shared point
means are 1.5–1.9× Direct on Linux and 3.4–3.7× on Windows; 2,000-row scan means
are 4–11% higher. Shared has moved closer to Direct but has not reached parity.

Other write-heavy/lifecycle results remain mixed. Linux .NET 8 single-write mean
is +19.8% [+1.4, +45.8] while CPU is −1.7%; fsync-sensitive tails vary widely.
Windows .NET 8 single-write mean is +3.6% [+0.8, +7.6] and transaction mean
+1.3% [+0.4, +2.4]. These signals are retained as regressions/uncertainty, not
converted into wins because a benchmark completed successfully.

### Simultaneous readers and writer progress

[Reader run 36276166668](https://github.com/litedb-org/LiteDB/actions/runs/36276166668)
uses `2f7a2350c` versus `d2fb099ac`, one/four reader processes, point queries,
50-row medium scans and complete 200-row streaming scans of roughly 800 KB.
It includes idle storage, continuous 200-document transactions, and a checkpoint
after each transaction. Each reader validates full payloads and snapshot consistency;
a cold indexed/collection check validates the writer's acknowledged revision.
WAL sampling runs in the Python controller, outside timed .NET operations, and
records its own CPU. Ten-millisecond peaks are sampled lower bounds.

| Four idle readers | Point aggregate ops/s before → after | Large scan aggregate ops/s before → after |
| --- | --- | --- |
| Linux .NET 8 | 6,894 → 80,927 | 982 → 2,530 |
| Linux .NET 10 | 12,175 → 192,887 | 1,399 → 3,615 |
| Windows .NET 8 | 5,348 → 73,052 | 594 → 2,945 |
| Windows .NET 10 | 5,834 → 79,491 | 579 → 2,432 |

These are medians of paired runs; the corresponding paired throughput intervals
exclude zero. With a continuous writer, four point readers complete 17–32 times
as much work, but writer throughput falls reproducibly. Linux .NET 10 point
writer throughput changes −17.1% [−18.4, −15.6], with writer p99 +20.7%
[+15.2, +26.4]. Windows .NET 10 large streaming readers improve 261%, while
writer throughput changes −24.1% [−25.5, −22.4] and writer p99 +40.3%
[+33.5, +46.9]. Checkpoint workloads also have writer losses, including Linux
.NET 10 medium-scan writer throughput −19.6% [−21.4, −18.0]. All workers
progress; these are throughput/tail tradeoffs, not observed starvation.

The [equal-offered-load comparison](https://github.com/litedb-org/LiteDB/actions/runs/36276205825)
uses the same production source with four readers at 50 calls/s each for point
and medium scans, or 20 calls/s each for large scans. It records intended-arrival
latency and offered/completed counts. Most writer-throughput intervals cross zero,
but Windows .NET 8 medium scans still show −4.2% [−7.6, −0.9], and Windows
.NET 10 large scans with checkpoints show −3.7% [−6.3, −1.1]. Windows .NET 10
medium scans have writer p99 +9.1% [+7.3, +11.4] while reader intended-arrival
p99 improves 35%. Several Linux aggregate CPU changes are +1–3%. Therefore
extra read work explains part, but not all, of the saturation regression.

**The task's strict performance gate is not met by mapped admission.** These
results require an explicit tradeoff decision or exclusion of this strategy.
Neither green CI nor full data validation waives that gate. Separate cache
eligibility experiments are evaluated against this source without changing it.
Every benchmark database validates, and every final WAL is empty; this establishes
eventual cleanup, not lower peak disk use. Raw peak/live WAL and per-worker
latency/resource results are retained, including uncertain and unfavorable cases.

### Writer contention and pin lifecycle

The `2f7a2350c` two/four-writer comparison includes a pinned writer, per-worker
API and intended-arrival latency, total CPU, lifecycle and final data validation.
Most paired aggregate-throughput intervals cross zero. Linux .NET 8 two-writer
throughput is +29.4% [+9.7, +51.3], while Linux .NET 10 two-writer CPU/call is
+11.1% [+2.4, +19.8]. Windows four-writer peer p99 rises +6.9% [+1.4, +14.7]
on .NET 8 and +19.7% [+5.2, +45.3] on .NET 10. These are not an overall
contended-writer win. Host disk latency varies substantially between campaigns.
Every worker progresses. The 5 ms offered peer stream overloads both versions,
so intended-arrival p99 includes queue delay and differs from API execution time.

[Fixed-yield experiment 36271353894](https://github.com/litedb-org/LiteDB/actions/runs/36271353894)
compares `175e855bf` with adaptive-policy `2c28cb85b`:

| Host/runtime | Two-writer aggregate throughput change | Four-writer change |
| --- | --- | --- |
| Linux .NET 8 | −38.0% [−41.0, −33.5] | −28.3% [−29.2, −27.5] |
| Linux .NET 10 | −28.5% [−32.1, −22.8] | −11.3% [−16.5, −3.2] |
| Windows .NET 8 | −35.3% [−36.7, −34.4] | −24.0% [−24.6, −23.5] |
| Windows .NET 10 | −32.9% [−35.1, −30.2] | −19.2% [−20.5, −17.7] |

The least productive worker completes 13–52% more calls and several peer tails
improve, but the aggregate loss rejects the candidate. The separate 2,000-cycle
thread start/join diagnostic has mean/p99 costs of 0.202/0.630, 0.151/0.238,
0.248/0.392 and 0.111/0.198 ms respectively. CPU/cycle is 0.295, 0.208, 0.422 and
0.164 ms. Against a minimum 10 ms quantum and 20 ms probe interval, this upper
bound does not justify another worker ownership/generation protocol.

## Mechanical cost breakdown

Separate temporary instrumentation of `d2fb099ac` and `9655832f2`, Linux/.NET 8,
1,000 operations after warmup. Instrumented times explain work; they are not
production speedup estimates. Nested phases overlap and must not be summed.

| Work / diagnostic | Parent → candidate |
| --- | --- |
| Point: read engine opens / closes during work | 1,000 / 1,000 → 0 / 0; candidate closes its last cached engine during lifecycle accounting |
| Point: admission mutex / full replay calls | 1,000 / 1,000 → 0 / 0 |
| Point: lease publication | 1,000 → 1,000; 1.77 → 1.34 ms total |
| Mixed (100 writes, 900 reads): write/read engine opens | 100 / 900 → 100 / 200 |
| Mixed: full replay calls / logical WAL input bytes | 1,000 / 200,704,000 → 300 / 60,211,200 |
| Mixed: replay / read-open / read-close time | 108.1 / 219.0 / 12.0 → 29.6 / 46.9 / 2.5 ms |
| Mixed: checkpoint attempts / time | 2 / 43.1 → 2 / 42.9 ms |
| Mixed: cache retirement | 100 calls, 1.29 ms total |
| Writes: full opens / replay calls / logical bytes | 1,000 / 1,000 / 200,704,000 on both |
| Writes: checkpoint attempts | 20 on both; 501.7 → 425.9 ms in this diagnostic run, not a paired performance claim |
| Incremental refresh calls / bytes | 0 / 0; no incremental writer state ships |

The short contention diagnostic spends about 1.0–1.6 seconds per worker waiting
inside ownership/pin acquisition, versus roughly 27–142 ms replay and 159–266 ms
checkpoint time. Full opens remain at handoffs. Durable commit ordering is unchanged;
these scopes do not isolate device flush latency. The selected read optimization
therefore does not claim to solve the parent's contended-writer loss.

## Resource and safety accounting

An intrusive `List<int>` free list adds at most 192 KiB payload at 65,536 slots,
retaining historical capacity until registry collection. Failed registration stays
retryable; failed release never links a slot back for unsafe reuse. Capacity,
irregular release, off-thread disposal and partial writes have focused tests.

A participating connection holds a 4 KiB mapping and OS-backed participation
handles. Cached read state owns no idle lease, expires after 100 ms, and is eligible
only below separate 4 MiB page-cache/opening-WAL limits. These are component limits,
not a total-RSS bound. A spill excludes retention; the resource test verifies actual
backing-file/stream disposal while the connection remains usable. Active streaming
readers keep their leases until disposal. Weak timer ownership, finalization,
explicit disposal, failed open and live-peer cleanup are tested.

A separate [64-connection ownership measurement](https://github.com/litedb-org/LiteDB/actions/runs/36276990204)
uses no growing latency sample list. After three seconds of reads, 1.5 seconds
idle and full GC, all 64 connections remain alive. Candidate extra live managed
memory is 334–395 KB total, about 5–6 KB per connection. After explicit disposal,
with connection objects still rooted, the difference is −19 to +43 KB for the
whole process. Linux open handles change from +196 while warm to +132 while
idle, then +4 after close; Windows differences are about +220 while alive and
+31–32 after close. Remaining process thread/handle counts include timer/runtime
infrastructure; they are not 64 retained cached engines. All generated files are
released on close, and cold data verification passes.

Linux .NET 8 PSS rises about 14.6 MB while warm and 3.1 MB after idle expiry;
Linux .NET 10 idle PSS is 1.4 MB lower. Windows idle working-set increases are
about 1.7–3.1 MB. Closing 64 connections adds 2.8–7.8 ms in medians across
platforms. These measured bounds apply to this workload, not arbitrary database
sizes or a total-RSS guarantee. The focused ownership tests separately establish
cached engine, spill stream and page-buffer release while the connection lives.

Fixed-duration throughput benchmarks do retain latency sample lists: faster
workers keep more samples. Their RSS/managed totals cannot isolate library cache
sizes or prove a leak, and process sums double-count shared mappings. Raw lifecycle
CPU, handles/threads, PSS where available and close timings remain in each result.

| Protection | Discriminating evidence |
| --- | --- |
| Final admission check after lease publication | Removing it reads a reclaimed WAL address and fails with `EndOfStreamException` |
| Structural publication before destruction | Removing it fails status visibility; bounded SC model covers 210 schedules, including interrupted publishers |
| All live generations observed before reuse | Ignoring leases corrupts the oldest snapshot in all four native plaintext/encrypted, graceful/killed-reader controls |
| Reuse epoch for excluded incremental prototype | Ignoring it loses an acknowledged peer update despite nonshrinking WAL |
| Fallback write revokes existing authority | Removing revocation returns stale post-acknowledgement data; raw files are copied before cleanup |
| Failed writable open balances publication | Injected open failure must leave readable status and the next open must mark its own structural change |
| Rebuild refusal precedes sidecar setup | Existing exhaustive install/rollback failure matrix remains unchanged and passes |
| Allocation-free publication | Old delegates allocate 320,000 bytes over 1,000 cycles; selected publication allocates zero |

Tests preserve the existing database/WAL format, checksums, commit barrier,
checkpoint thresholds and recovery assertions. The mapped page is ephemeral,
never durability evidence. The tested fault model includes process death,
partial/failed ephemeral writes and existing persistence/recovery injections;
finite tests do not prove arbitrary device/filesystem failure behavior.

Historical raw results, instrumented patches, negative controls and unchanged
synthetic failure images are preserved in the [evidence archive](https://github.com/litedb-org/LiteDB-Artifacts/tree/deb839da3e4f0ed9e42249d931f683d1e5781c37/pull-requests/3014-shared-performance).
Its manifest records original/published hashes and path-only text sanitization.

## Bounded campaign and reproduction

Build `LiteDB.Fuzz` with `TestingEnabled=true` in an isolated test checkout, then:

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

Smoke selects seeds 3012–3014 with four steps; extended selects 3012–3027 with 64,
for snapshot/shared/MVCC-retirement/index. Each target/seed has its own artifact
partition and still replays built-in regressions. Discovered inputs are retained
per partition and can be rerun individually with `--replay <replay.json>`; they
are deliberately not fed into every later seed in this finite campaign.
Sessions are serial and bounded
below 300 seconds, with a 256 MiB runner cap, 512 MiB externally accounted campaign
cap and 10 GB total local task budget. Private TMPDIR is ext4 on Ubuntu 24.04 x64;
local runtimes are .NET 8.0.30/10.0.11. Other users' host workloads are uncontrolled.

The older `2c28cb85b` campaign stopped when accumulated interesting-corpus replay
exceeded the session budget; its primary failing-session seed had passed before
replay. Logs and files were preserved, and per-seed partitioning fixed the harness
budget problem. Subsequent partial campaigns were explicitly superseded by source
fixes, between native invocations. None is reported as a complete final campaign.
The immutable, clean `2f7a2350c` checkout completed all 12 smoke invocations
(48 primary steps plus six built-in replay runs) and all 64 extended invocations
(4,096 primary steps plus 32 built-in replay runs). There were no skips, timeouts
or failures. Maximum invocation duration was 126.44 seconds; peak externally
accounted campaign output was 47.1 MB. Extended primary observations include:

- 3,072 snapshot validations and 7,168 checkpoints between reader generations;
- 1,024 Shared rounds, 3,072 child processes, 60,221 acknowledged rows and
  1,024 integrity checks;
- 416 observed crash positions and 224 internal crash-point observations;
- 1,024 retirement steps and 1,024 index steps, including 134 key-moving updates.

These are summed observations, not claims of globally unique states. The separate
four-platform production state campaigns also pass for both parent and candidate:
each runs four epochs, eight conflicting transactions, four killed writers,
four killed readers, four rollbacks and 12 generation validations, then validates
revision 31 through full documents, indexed access and read-only physical integrity.

[Hosted CI 36276146073](https://github.com/litedb-org/LiteDB/actions/runs/36276146073),
[fuzz 36276146011](https://github.com/litedb-org/LiteDB/actions/runs/36276146011) and
[index migration 36276145937](https://github.com/litedb-org/LiteDB/actions/runs/36276145937)
are green on this source. Reconciled evidence covers:

| Matrix | Executed results |
| --- | --- |
| .NET 8/9/10, Linux x64/ARM64 and Windows x64/x86 (18 jobs) | 4,657 passing result rows and seven existing skips per job |
| .NET 8/9/10, macOS ARM64 (three jobs) | 4,658 passing rows and seven existing skips per job; one additional platform errno case |
| Framework 4.6.2 / 4.8.1, actual native xunit console hosts | 4,482 passed / nine existing skips each; console evidence, no TRX uploaded by this workflow |
| Windows native cross-process matrix (12 jobs) | 33 passed per job, plus 50 x86 repeat cases and one dump-contract case |

Modern jobs each contain all ten partitions, including a passing runtime/architecture
and test-hook gate in each partition. Counts include those intentional repetitions;
parameterized cases can share a display name. Runtime output confirms actual
8.0.31/9.0.20/10.0.12 hosts and x86/x64/ARM64 as requested. The modern seven skips
are the existing collation/current-culture, parallel query, disk scheduler,
Issue2127 long-running insert, spanning-vector persisted update, rebuild culture
and predicate-builder cases; Framework additionally skips its ObjectId/Core
contract and Issue2298 constructor case. Raw reports retain exact skip reasons.

The Windows x86 timeout artifact is the intentional dump-contract smoke: its first
attempt validated a 163,610,711-byte full 32-bit process dump, then removed that
successful dump. Diagnostic text is retained; it is not an unexpected database
hang. Existing Framework full-suite sessions use their unchanged workflow timeout;
the bounded local campaign and modern partition sessions retain the 300-second
limit. No broader device/power-failure guarantee follows from this finite evidence.

The PR remains draft because the mapped strategy's performance gate is unresolved.
Mixed-version interoperability is not a blocker or a claimed supported feature.
