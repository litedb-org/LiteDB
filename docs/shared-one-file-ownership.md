# Mapped Shared ownership transitions

Mapped and legacy Shared use the same native data admission as Direct; see
[Direct native admission](direct-process-host.md#native-admission), including the
restricted Darwin read-only fallback. Both Shared modes publish shared admission
before checking the Unix read-only Direct marker at admission + 2. Only then do
they claim their family byte (+ 1 legacy, + 3 mapped) and probe the opposite
family. This permits compatible Shared peers while excluding Direct and mixed
Shared families. The mapped family must never occupy the read-only marker byte.
Stop all participants before changing experimental protocol versions; no live
upgrade or coordination-file migration is implied by this merge.

The Direct host registry and its per-host lifecycle gate own Direct facade
cleanup, maintenance and final storage close. Mapped Shared keeps its separate
per-identity participant gate; internal cores bypass Direct host attachment.
Final participant release closes retained results/cache and mapping/slot/DMS
before native admission. A closing participant cannot be replaced until its
actual teardown completes. Neither registry holds a global publication gate
while waiting for final teardown of one file.

The mapped identity key cannot currently change while a participant is live:
explicit rebuild and file-replacing upgrade/auto-rebuild are refused before
replacement. Enabling those operations requires stable admission-entry registry
keys and a participant maintenance gate before any replacement can be published.
Legacy Shared has no retained core registry: main serializes rebuild with peer
operations, and idle peers reopen the current files rather than reusing a stale
core. Native admission retains the same entry across replacement for those peers.
`SharedOneFileRekey_Tests` checks all four legacy native rekey stages, repeated
plain/encrypted rebuilds, live peer visibility and cold indexed payloads; it also
checks every mapped replacement refusal without data/WAL mutation.

The participant-local transaction lease, named main mutex and native WRITE byte
have nested lifetimes. Main orders every writable core, including recovery and
teardown. WRITE protects the entire writable core tenure, not just its explicit
transaction. A pin may keep that core after a transaction finishes; its holder
can drain independently of the calling thread. A committed leased snapshot owns
its exact reader generation and can continue without main or WRITE.

The transaction transition is:

1. **Idle → reserved.** `BeginTransCore` reserves a generation-tagged local lease
   before waiting for native main. The reservation stays in that call's local
   variable. It is not published in the facade that an old holder is cleaning.
   A repeat Begin on the same actual `Thread` borrows the active reservation.
2. **Reserved → active.** After native acquisition and a successful core Begin,
   publish the reservation with the transaction's actual `Thread` and retained
   native recursion or pin hold. A false Begin is a join: it returns the call's
   use and any new reservation, leaving the surrounding transaction intact.
3. **Active → ending.** `EndTransaction` captures the exact lease and native hold,
   clears facade transaction state, then returns the hold. A `finally` returns
   the captured local lease and pulses queued local waiters. Repeated or stale
   disposal cannot release another local generation.
4. **Ending → idle.** The final core use closes storage, publishes a healthy
   fence or leaves an uncertain fence, and releases WRITE in either case. Native
   main remains held through that teardown and only then admits a successor.
   Operations reconcile an inner implicit abort or Stop before returning their
   extra native use. The former core may already be closed when the call fails.
5. **Abandon.** Ordinary owner-exit and pin-holder cleanup close their core while
   native main is still owned. Both run the same terminal transition in `finally`.
   They have already drained the native use count, so they do not return an
   additional recursion. Local waiters also periodically recheck the retained
   actual `Thread` object; a dead reservation can never depend on a future caller
   entering the gate. Native main and actual WRITE acquisition still precede any
   successor storage access.
6. **Dispose.** Refuse new operations, drain admitted work, close the core and
   WRITE, then release the facade's participant reference and data admission.
   Dispose from a running pinned callback requests a forced drain and transfers
   this entire sequence to the holder. It must not detach `SharedMemoryReaders`
   while the holder still needs its table to release WRITE. Cleanup failures
   cannot skip terminal lease release or the remaining facade resource releases.
   Reentry from an executing teardown callback refuses before changing state.

Retained streaming results extend call admission through every `Read`, including
transforms executed after `Query` returned. A pinned holder waits for these reads
before closing its core, independently of transaction/result holds that a forced
dispose can invalidate. Dispose invoked inside an ordinary admitted callback
defers cleanup to that thread's outermost call exit. A pinned callback transfers
cleanup to its holder. In both cases the callback and underlying read return
before teardown starts. Stale results fail their native generation/pin check and
cannot enter a successor core. Holder teardown waits only for retained reads;
unrelated facade calls may already be waiting for that holder to release main.

Dead-owner handoff can overlap a new reserved call. Cleanup only sees the old
published lease; it cannot erase a successor's pending reservation. The local
generation and native holder generation independently reject stale result/lease
cleanup. Reader ownership and transaction ownership retain `Thread` references;
managed thread IDs are not ownership identities.

Same-thread foreign-transaction refusal belongs immediately before a native
acquisition, including cold pure queries and pin creation. It does not precede
independent mapped snapshot admission or iteration of an accepted committed
snapshot. Another actual thread can wait and resume after completion.

An abandoned main mutex does not establish process death or free WRITE. A
successor must lock the real byte. Only an abandonment hint permits its bounded
five-second retry; a real held byte still excludes storage access. Constructor
validation and writable query fallback carry that hint as ordinary and pinned
opens do. Final-dispose checkpoint deliberately defers abandoned recovery to a
later protected open. A timed-out retry owns no WRITE and can be retried after
actual release. A live holder releasing during the window is indistinguishable
from delayed kernel close; this is an exclusivity policy, not liveness proof.

`SharedOneFileSlice7*`, `TerminalTransitions`, `DeferredRead`, `OwnerGeneration`, `ReadOwnership`
and `RetryHandoff` tests discriminate these transitions. `ProductionReuse` kills
real SharedEngine constructors/closures twice at publication, Ready, WRITE and
DMS boundaries; `ReplacementReplay` and `LiveDimensions` cover stale same-path
metadata and live mapping lifetime. See the corresponding SharedOneFile test
classes and the `shared-one-file` contract registry for executable evidence.

This opt-in remains qualified only for Linux x64 on modern runtimes. These are
exception, thread/process death, corrupt-input and forced-interleaving models.
They do not establish Windows/macOS or ARM/x86 mapped execution, Framework mapped
execution, device power loss or whole-path mapped power-loss fuzz qualification.
Transaction handles and supported cross-thread completion are Phase 4 work;
legacy explicit transactions still complete on their creating Thread.
