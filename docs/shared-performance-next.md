# Shared-mode performance after #3013

Work in progress. PR #3014 is stacked on #3013, currently
`d2fb099acebb58dbbb07acbeea49c05bb025defd` (initial measurement baseline
`4cff52c4074a61bea5a283b513dbbfe526c16b86`).

**Updated concurrency contract:** all processes concurrently opening one database
in Shared mode must use exactly the same LiteDB version. Concurrent mixed-version
access is unsupported. This removes the legacy-writer participation objection to
mapped admission and incremental reopen. Both strategies have implemented prototypes. The selected composition is mapped
reader admission plus the free list; incremental writer replay did not meet the
performance gate.
Persisted database compatibility, durability and the other safety gates still apply.
The free-list measurements establish only a helper improvement, not completion
of the architectural read-admission and writer-handoff objectives.

## Candidates and safety boundaries

| Strategy | Decision and evidence |
| --- | --- |
| A: mapped admission | Selected for PR #3014, with final qualification pending: warm reads publish a lease and validate aligned atomic storage epochs without taking the database mutex. Every same-version writer participates. Cold opens and unsupported environments retain the protected path. Platform performance and safety qualification are in progress. |
| B: incremental reopen | Excluded after five-pair Windows/Linux .NET 8/10 comparisons: no repeatable contended-throughput gain, approximately 38 KB additional allocation per write, and several reproducible write/checkpoint regressions. The isolated branch preserves the implementation and fixes/tests for the retirement/safepoint defect found by the native campaign. |
| C: reusable pin holder | Measurement pending. `SharedMutexOwner` already reuses its worker for one second of idle time and detects logical owner death. `SharedMutexPin` creates a thread per pin. A thread-only production diagnostic separates this cost from close/open, WAL replay and sync. |
| D: adaptive yielding | No policy change selected. The parent already sets the quantum to at least measured open + previous close cost (minimum 10 ms), probes every 20 ms and retains the turnstile. Further experiments/evidence remain pending. |
| E: slot selection | Candidate: replace `List<bool>.IndexOf(false)` with an intrusive free list in `List<int>`. Registration and release remain under the same lock; all existing file writes and ordering remain intact. Production comparisons pending. |

An additional retained integer per slot replaces the Boolean: at the existing
65,536-slot cap the payload increase is at most 192 KiB per saturated registry.
No thread, handle, mapping, lease lifetime, WAL retention, or idle work is added.
The list keeps its historical capacity until the registry is collected, as before.
The acceptance budget for this candidate is that 192 KiB maximum payload increase;
actual allocations and lifecycle costs are measured separately.

## Invariants and discriminating tests

| Invariant | Code and evidence |
| --- | --- |
| A returned lease has published protection | `Lease` unlinks a free entry only after slot and count/complement writes succeed; existing partial-write and header-retry tests apply. |
| An unsuccessful registration remains retryable | The entry stays on the free list on failure. The capacity/failed-republication test fills all 65,536 slots, fails reuse and succeeds on the next attempt. |
| A failed release never authorizes reuse | `Release` links an entry only after `WriteSlot` returns. New fault cases inject 0, 4 and 8 bytes before throwing and assert the next publication does not overwrite that slot. The torn case requires an unknown registry result. |
| Every live generation remains protected | Churn uses an independent expected-version array through irregular release order and off-thread disposal; the existing three-generation native tests exercise reclamation with complete payload/index oracles. |
| Lease identity/lifetime remains stable | Duplicate disposal remains guarded by `Interlocked.Exchange`. Disposing the slot owner while readers remain retains the file pair through their last release. |
| Compatibility/fallback | File format, parser, registry path, database mutex, count/complement checks, legacy leases and fallback selection are unchanged. Concurrent participants use the same LiteDB version; mixed-version concurrency is unsupported. |

The test fault model is failed/partial ephemeral lease writes and process lifetime;
these are not simulated host power loss. Durable database/WAL writes, flushes,
checkpoint and recovery are untouched. Existing persistence-fault regressions still
need to pass as part of acceptance.

## Reproduction and evidence

Build separate production checkouts using `TestingEnabled=false`, and build the
existing `tools/SharedReadBenchmarks` runner against each library. The runner rejects
hook-enabled assemblies. `scripts/measure-shared-slots.py` performs five alternating
pairs per workload with a fresh process, full payload validation, and final-close
accounting. Helper measurements at 1/64/4,096/65,536 active slots isolate selection;
they are not end-to-end application throughput claims.

Run the campaign after building `LiteDB.Fuzz` with hooks:

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

The first command selects seeds 3012–3014 with four steps; the second selects
3012–3027 with 64. Each target/seed is a separate session bounded below 300 seconds,
serial, with a 256 MiB runner cap and 512 MiB external cap. Failures stop the script
and retain commands/logs/artifacts. Requested coverage is not completed coverage;
results must be reconciled with target metrics and replay manifests.

Local environment: Ubuntu 24.04 x64, .NET 8.0.30 / 10.0.11, ext4; private `TMPDIR`
on the same volume as `/tmp`. Other users' host workloads run concurrently and are
not controlled by this task. The full supported platform matrix and final
composition campaign remain acceptance gates. Concurrent mixed versions are
outside the supported contract.

### Initial Linux .NET 10 production comparison

Five alternating pairs against the frozen parent; library source at `b33c00733`.
The median helper latency at the 65,536-slot cap fell from 3.051 to 2.099 µs;
the mean paired relative change is −33.0%, with a deterministic paired-bootstrap
95% interval of −35.7% to −30.9%. Allocation stays 32 bytes per replacement.
This is a helper result, not a claim that Shared application throughput rose 33%.
At 4,096 slots the paired interval crosses zero (−17.2% to +2.7%).

Ordinary workload medians (ms/op): point 0.0965 → 0.0924, scan 2.202 → 2.244,
mixed 0.960 → 0.918. Paired intervals all cross zero and are broad; these results
neither establish improvement nor rule out a regression in those workloads.
The scan mean paired change is +9.8% [−5.2%, +25.5%], despite the much smaller
median difference. Shared-host drift matters; report the pairing and uncertainty
rather than selecting favorable medians. All runs validated results and final WAL
cleanup. Hosted comparisons and the other runtime remain necessary.

### Same-version experiments, September 26

These measurements precede the final read-cache demand refinement. The
refined admission source is `fcea72a116f32b0c11b9353b716f8a5a63dc94e4`;
benchmark head `7d1fc6d80eec4def1e87861c615bd784b80ce195` adds harness inputs.
The comparator is `6d71e39bb180ef671ecf5fd4c90262b6d14666b4` (free-list only).
[Hosted production evidence](https://github.com/litedb-org/LiteDB/actions/runs/36269004080)
uses five alternating pairs on each host/runtime; percentages are paired mean
latency changes with deterministic bootstrap 95% intervals.

| Host/runtime | Point | 2,000-row scan | Mixed read/write |
| --- | --- | --- | --- |
| Linux .NET 8 | −80.6% [−80.9, −80.2] | −39.0% [−39.2, −38.7] | −57.7% [−58.9, −56.7] |
| Linux .NET 10 | −79.4% [−79.8, −79.0] | −44.5% [−45.7, −43.4] | −58.6% [−59.4, −57.5] |
| Windows .NET 8 | −79.4% [−80.2, −78.8] | −45.7% [−47.3, −44.2] | −59.3% [−61.4, −57.5] |
| Windows .NET 10 | −82.0% [−82.2, −81.6] | −40.4% [−42.3, −38.4] | −53.3% [−58.3, −48.3] |

Point-read allocation fell from approximately 278–280 KiB to 26 KiB per call.
These results do not establish the complete acceptance matrix: write-heavy,
connection lifecycle, retained resources, simultaneous readers and incremental
writer results remain under review. An earlier admission implementation was
rejected after open/use/close latency increased roughly eightfold; lazy authority
creation, no absent-authority cleanup acquisition, and nonthrowing marker probes
addressed its identified overhead. Its older timings are not final-candidate
measurements.

Local .NET 8 negative controls removed one protection at a time from isolated
checkouts. Skipping final admission validation produced an `EndOfStreamException`
from a reclaimed WAL address. Omitting structural publication failed the status
visibility assertion. Ignoring published leases corrupted the oldest observed
snapshot in all four native plain/encrypted, graceful/killed-reader cases.
Ignoring the writer reuse epoch lost an acknowledged peer update despite a
nonshrinking WAL. Protected variants pass their corresponding tests; failed
patches, commands, TRX files and database images are preserved locally. The
structural-publication control is a protocol assertion, not a claim of observed
native data loss.

The combined prototype passed 111 focused tests, followed by 12 native writer
append/checkpoint-death cases with complete document, index and repeated-cold-open
checks. Broader regression testing found that blocked rebuild opens could create
coordination sidecars before checking the recovery marker. That product regression was fixed by checking the recovery marker first. The
existing matrix and writer regression tests then passed (69 tests). The complete
initial combined run had 12 failures, all in that rebuild matrix; its other
partitions passed. No existing recovery assertion was relaxed. Final-composition
full-suite validation remains pending.

The writer campaign additionally found a checksum failure after partial checkpoint,
resume and uncommitted writes. Captured live query indexes omit intermediate
committed safepoints that full recovery indexes; inferring free slots from the
smaller index reclaimed frames without witnesses for their latest incarnation.
The fix preserves the captured allocator when the complete prefix fence is valid.
A focused plaintext/encrypted reproduction fails before and passes after the fix.
This experiment remains excluded for performance reasons, independent of that fix.

The admission refinement starts retention only after two consecutive read-only
opens and retires idle cached handles before writable operations. It targets the
measured 2.7–6.1% alternating-read/write overhead and an extra Windows handle pair.
Its 90 focused/native/rebuild tests pass; the lifecycle fixture now performs the
third read needed to exercise reuse under the new demand policy. Final production
comparisons must establish that the refinement closes those performance concerns.
