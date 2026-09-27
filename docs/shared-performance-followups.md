# Shared-mode performance follow-ups (#3012)

Based on #3003 at `a81456695`. The three prototypes are kept as separate commits,
then hardened together. This changes mutex admission and ephemeral reader leases;
the database/WAL format, recovery protocol and durability barriers stay the same.

## Ownership and compatibility

Scoped operations acquire the native mutex on the calling thread. Transactions,
write queries, pins, arbitrary input enumerables, read transforms and custom streams
use the holder thread. Array inputs can use scoped writes. A lazy input can open a
reader that survives its write call; its off-thread disposal remains supported.
Small reads probe lease availability once. Subsequent scoped reads publish a lease
before executing any query. Failed registration retries through the holder before
execution, preserving bounded buffering and the streaming/disposal contract.

A second named mutex orders competing acquisitions. A blocked participant holds
the turnstile while waiting for the database mutex. Pins probe it every 20 ms and
yield after their reopen cost (at least 10 ms), once operations and explicit holds
drain. Nonblocking acquisitions also respect it. A dead turnstile owner protects no
database state; abandonment of the database mutex still triggers normal recovery.
This is not strict FIFO scheduling or a hard latency guarantee. Under contention,
the pin owner pays for more engine reopens so the other participants can progress.

One exclusive `.lease` handle proves the connection's liveness. Its `.slots` file
holds every reader version as `(version, ~version)`, with `(0, 0)` for a free slot.
A count/complement header detects whole-slot truncation as well as empty content.
Publication is under the database mutex; releases may run concurrently with an
inspection. A torn release is unknown or retains the old version, both conservative.
Missing, malformed, oversized or unreadable content skips reclamation. A failed
release never reuses its slot. The bounded file supports 65,536 concurrent slots;
exhaustion falls back to mutex-protected streaming. Files outlive connection disposal
while any reader still needs them, and native locks release on process death.

Failed registration makes a best-effort rollback of unpublished slot bytes and
the count header, preserving the original I/O error. Cleanup failure remains
conservative. Rollback never extends a file shorter than its published length:
zero-filling missing slots would hide live versions. Such a connection refuses
new leases and preserves the unknown metadata until its readers finish.

Legacy per-reader leases are still understood. #3003 readers encountering the
`slots-` prefix conservatively skip checkpoints and rebuilds, even for an idle new
connection, until that connection closes. Mixed-version operation sacrifices
reclamation throughput. Only generated slot-content names are cleaned up.

## Acceptance evidence

| Invariant | Discriminating coverage |
| --- | --- |
| Escaped readers keep off-thread disposal | `SharedScopedCalls_Tests`: lazy write input, read-transform reentry, registration failing after a successful probe |
| Scoped ownership excludes peers and recovers | `SharedMutexOwnerScoped_Tests`, `SharedFollowupProcess_Tests`: recursion, release/dispose, dead thread, killed native owner, queued peer, acknowledged data |
| Every snapshot generation remains protected | `SharedSlotGenerations_Tests`: three versions in one connection/process, oldest-first departure, real WAL-slot clearing, repeated writes/checkpoints, reader death, encryption, cold full-payload and indexed verification |
| Bad lease metadata cannot permit reclamation | `SharedReaderSlots_Tests`, `SharedReadPath_Tests`: partial writes/releases, failed header publication/retry, truncation, malformed/missing content, legacy leases, unrelated-file preservation |
| Contenders progress and death cannot wedge admission | Native process tests kill a waiter while it owns the turnstile; a pinned loop with a one-minute hold limit must admit another writer within the bounded test |
| Existing storage guarantees remain intact | Shared/MVCC crash-boundary, abandonment, disposal, ownership, checksum and durability regressions; full platform CI |

Negative controls run in an isolated checkout, never in the product: publishing only
the minimum version produces WAL checksum failures in all four generation scenarios;
allowing arbitrary lazy inputs to take scoped ownership makes off-thread disposal fail.
The turnstile negative control disables the pin's waiter probe.

The first full run also exposed stale admission cleanup after disposal touching a
new mutex owner. A deterministic ownership test now forces that transition. The
shared process campaign found a harness-only replay failure: its trace hash included
native PIDs. Trace events now use stable worker identities, while `processes.jsonl`
retains native PIDs for diagnostics; a real two-run test checks the replay contract.

The bounded campaign uses seeds **3012, 3013, 3014**, four steps per target,
`snapshot,shared,mvcc-retirement,index`, one worker, and a 256 MiB artifact budget.
The snapshot target now keeps three generations simultaneously, mutates overlapping
keys over repeated reclamation cycles, compares full current documents and indexed
order against an independent mutation model, and checks physical structure after
quiescence. Native process-death tests and the persistence fault model remain distinct:
a killed process does not simulate losing the OS page cache or a lying device.

Snapshot mutations and checkpoints run in a separate writer process. Each command
has a 15-second progress deadline; timeout terminates the writer and live reader
processes, reporting `SNAPSHOT_WRITER_TIMEOUT`. A regression holds the native writer
mutex, forces that timeout, checks child termination and verifies committed data.
This bounds actual database calls as well as command transmission; no blocked
writer task remains inside the controller.

Shared-process scenarios targeting a checkpoint crash hook retain the crash
worker's native mutex ownership across commit and checkpoint. Otherwise a peer
can empty the WAL in that gap and prevent the selected hook from running. Peers
still contend and recover after the worker dies; other crash boundaries keep
their ordinary interleavings. The guard has native-lock positive/negative controls,
and the campaign requires every configured marker as well as recovered contents.

Runs use Release with `TestingEnabled=true`, Linux x64 and a private `TMPDIR` on
the original temp volume. CI covers Linux, ARM64, macOS, Windows x64/x86 and Framework;
the native child harness runs on modern .NET. Each CI partition retains its 300-second
session timeout and the partition verifier checks that every method is assigned once.

## #3003 evidence clarification

The fetched Linux .NET 8 job `108280261329` in run `36198429286` at `a81456695`
contains four passing orphan scenarios and no `PASS-ON-RETRY` diagnostic. Its source
does not retry correctness assertions. The reported 900-versus-0 retry could not be
corroborated in that job; it is not treated as a proven data-loss bug or waived failure.
That run's actual Windows x86 failure was the internals partition reaching its
300-second timeout while tests continued completing. MVCC now has its own partition.
Coordinator documentation also distinguishes its existing real killed-host test from
the broader takeover campaign still needed for production promotion.
