# Mapped Shared ownership transitions

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

## Phase 3-A writer tenure

A writable mapped facade uses a dedicated native mutex holder from its first
writable open, including legacy BeginTrans. While alone, it retains main, WRITE
and the same core between calls and while idle. Idle tenure can move to another
application Thread; a running call, explicit transaction or retained reader still
belongs to its actual Thread. A sibling facade requests a boundary handoff through
the participant; facade callbacks/settings and stale-result generations stay
isolated. The unmapped path retains its previous scoped/pinned behavior.

The existing pin gate serializes operation entry and handoff. It counts at most
eight completed outer operations (therefore no more than eight transactions) or
one monotonic millisecond after observing contention. `SharedWriterTenure`
contains the internal budgets. An attached live process also enables the budget,
even without a writer request. A budget never interrupts a transaction or a
stream still retaining the writable core. New top-level calls stop entering at
exhaustion; the holder closes under main, ends WRITE exactly once, then releases
main. Stopped cores request forced drain before returning the failing operation;
their odd fence remains until independent protected recovery. Durable group
publication, structural/reuse fencing and reader admission rechecks are unchanged.

Request records use reserved ABI-2 process-block bytes 80 (ticket) and 88 (expiry),
with a shared sequence at control+1032. Each participant publishes its oldest
pending ticket before waiting for main, including constructor validation and cold
readers. One renewal timer per requesting process extends a 100 ms lease every
25 ms. Nested/local waiters share the record; only the last release clears it.
A requester waits behind older unexpired process tickets before entering the
existing native turnstile. The releasing writer must also publish a fresh ticket,
so it cannot repeatedly barge ahead of a live waiting process. Expired, dead,
not-Ready and out-of-range expiry records cannot reserve a turn. An alive process
whose renewal is suspended can lose its position and retry; liveness assumes
scheduled renewal and bounded transactions/retained results. Requests authorize
no storage access. Abandonment still requires actual native WRITE acquisition.

Linux x64 uses shared `FUTEX_WAIT`/`FUTEX_WAKE` on the low aligned 32 bits of
control+1024. Request publication increments the mapped wake sequence before
waking the holder. The holder samples the word, rechecks pressure and waits on
that sampled value, so publication racing wait cannot lose the wake. A 20 ms
maximum wait checks application-owner death; under contention it waits at most
1 ms. This is a scheduling/liveness bound, not a real-time OS guarantee. Local
forced drain wakes the same futex. No holder probes the turnstile while alone.
The full mapping stays pinned through native holder release and request lease disposal;
both edges retain their own participant reference, independently of facade disposal.

Windows qualification must implement an auto-reset named kernel event scoped by
the validated authority UUID and mutex namespace, with matching access control.
`SetEvent` after sequence publication and `WaitForSingleObject` between predicate
rechecks provide the same retained wake semantics; `WaitOnAddress` is not a
cross-process substitute. macOS qualification must use an authority-scoped POSIX
named semaphore (`sem_open`/`sem_post`/`sem_wait`), coalescing posts with a
shared pending bit and unlinking only at protected offline retirement. A holder-local
timer posts the same semaphore at the bounded liveness deadline (20 ms alone,
1 ms under pressure); do not assume Darwin provides POSIX `sem_timedwait`. Both need
crash/recreation/name/permission and lost-wake tests before enabling them. This
slice still refuses mapped mode outside Linux x64; no Windows/macOS/ARM execution
or power-loss qualification is claimed by compiling those targets.

Native reference semantics: [Linux futex](https://man7.org/linux/man-pages/man2/futex.2.html)
uses an aligned shared 32-bit word; [Windows WaitOnAddress](https://learn.microsoft.com/en-us/windows/win32/api/synchapi/nf-synchapi-waitonaddress)
wakes only within one process. Native execution remains a qualification requirement.
