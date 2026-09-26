# Shared reader slot selection after #3013

Work in progress. PR #3014 is stacked on #3013, currently
`d2fb099acebb58dbbb07acbeea49c05bb025defd` (initial measurement baseline
`4cff52c4074a61bea5a283b513dbbfe526c16b86`).

**Updated concurrency contract:** all processes concurrently opening one database
in Shared mode must use exactly the same LiteDB version. Concurrent mixed-version
access is unsupported. This removes the legacy-writer participation objection to
mapped admission and incremental reopen. Both strategies are being re-evaluated;
the earlier rejection rationale below is historical, not the final decision.
Persisted database compatibility, durability and the other safety gates still apply.
The free-list measurements establish only a helper improvement, not completion
of the architectural read-admission and writer-handoff objectives.

## Candidates and safety boundaries

| Strategy | Decision and evidence |
| --- | --- |
| A: mapped admission | Excluded: an unmodified #3013/#3003 writer does not publish the page and can join later. Its checkpoint scans registrations under the named mutex. A speculative reader outside that mutex can publish after the scan, then open reclaimable storage. A new marker ignored by old writers cannot exclude that interleaving. Holding the existing mutex for the whole lifetime removes the proposed benefit. No candidate path or experimental switch is enabled. |
| B: incremental reopen | Excluded for the proposed fence: `DiskService.AllocateLogPosition` can consume `_freeLogPositions` below the cached boundary without updating the header salt/root. `_signals?.SlotReused()` is optional and ordinary older Shared writers do not supply it. A matching header, nonshrinking length and unchanged old confirmation therefore do not prove an append-only prefix. The coordinator's `TryRefresh` depends on an epoch all its writers honor; that precondition does not hold here. No stale engine/cache survives a handoff in this patch. |
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
| Compatibility/fallback | File format, parser, registry path, database mutex, count/complement checks, legacy leases and fallback selection are unchanged. Mixed-binary validation is pending. |

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
not controlled by this task. Windows, macOS, ARM64, x86, Framework, mixed binaries,
negative controls, full matrix and hosted CI evidence remain pending.

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
