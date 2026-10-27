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
Ownership tests must observe the actual native tenure: an ordinary commit or
rollback can leave a healthy mapped core retained. Require and await a real
handoff before asserting WRITE is free or injecting a delayed Stop for a retired
core. Reader-generation oracles cover both transformed protected reads and the
default warm-cache path; enabling warm admission does not relax exact snapshot,
index or unrelated-data assertions.

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
with a shared sequence at control+1032. Each participant serializes local native
acquisitions with a FIFO request queue. A ticket belongs to one process request,
never to the number of local callers. Its transitions are:

1. **Queued:** the call owns a local queue node and a participant reference, but
   publishes no ticket and cannot enter native acquisition.
2. **Published:** the head allocates a fresh global ticket, publishes its 100 ms
   expiry, then wakes holders. One timer renews this request every 25 ms. Younger
   local calls cannot borrow its ticket or pass its native admission.
3. **Admitted:** after observing no older unexpired Ready process request, the
   head enters the native turnstile/main wait. The advisory record stays alive
   until that native acquisition returns; only main plus real WRITE permit storage.
4. **Retired:** successful acquisition, refusal, exception or cancellation removes
   this node and clears its record, independently of outstanding local callers.
   The next head publishes its own fresh ticket. A releasing holder queues a new
   request; it cannot reuse a waiting caller's priority.
5. **Expired/re-entered:** renewal or a resumed waiting call finding its expiry
   passed allocates a fresh ticket. It cannot resurrect its former priority ahead
   of already published successors. Renewal failures withdraw the record and
   report through the waiting caller, rather than escaping a timer callback.

Expiry validation loads the peer expiry before sampling the current upper bound;
normal renewal after the caller's earlier clock sample cannot invalidate an older
request. Expired, dead, not-Ready and genuinely out-of-range records reserve no
turn. Liveness assumes scheduled requesting threads and renewal, and bounded
transactions/retained results. A stalled head cannot authorize younger local
writers. Native turnstile scheduling after advisory admission is still OS owned.
Abandonment always requires actual native WRITE acquisition.

A callback already operating the current core's transaction joins nested
BeginTrans before reserving a new local transaction lease. A pending reservation
may itself be waiting for that callback to release main; waiting behind it would
form a cycle. Sibling facade callbacks still refuse before waiting, and a false
Begin join owns no new explicit lease or completion responsibility.

Every new pinned operation (including nested writes, Begin and retained-core
queries) passes the facade admission under its disposal lock. The pin operation
is taken first to protect that check from teardown and is returned on refusal.
An already admitted call may finish after its callback invokes Dispose; that
permission does not admit another call. Reused write pins also validate the live
mapped authority before touching the core, matching ordinary writable opens.
Disposed mapped participants refuse before native acquisition rather than following
a cleared table reference. Explicit rollback after disposal retains
its existing harmless no-transaction behavior. New pin publication admits under
the same lock. While waiting for local/request priority, facade disposal cancels
admission and unwinds the request's mapping reference even if an older remote
request renews continuously. Native main waits already admitted before disposal
retain the existing drain policy; no cancellation of an OS mutex wait is claimed.

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

## Phase 3-C maintenance and final close

Mapped replacement takes main and WRITE as ordinary writer tenure does, then a
canonical-path admission mutex and bootstrap. Bootstrap is tried without waiting:
attach can already be requesting main. Under bootstrap, an atomic DMS conversion
must establish that this process is the only participant, including not-yet-Ready
attachers. More than one local facade or any retained reader generation also
refuses. Admission remains exclusive through replacement and rekey; a peer must
close before retrying. `SharedOneFile` remains opt-in, default false.

Ordinary `Checkpoint()` keeps its existing partial backfill behavior with peers.
Only the engine's existing WAL truncation branch tries bootstrap and exclusive
DMS; contention retains the WAL and its incarnation while still allowing partial
backfill and safe obsolete-frame reclamation. The truncation lease remains held
through structural publication. An outer exclusive bootstrap, maintenance or
final-close scope is borrowed without downgrading it. Legacy WAL format conversion
holds this lease across drain and reinitialization, even for an initially empty
WAL. No public option is added.

The existing rebuild installation/rollback protocol retains the original data/WAL
pair and completed candidate. ABI-2 extends its guard with a checksummed immutable
record of the exact paths and file identities. A pending record is flushed and
renamed to the guard; its directory is synced before any installation rename.
Each rename is followed by a directory sync. A death before guard publication
cannot have changed the original pair. An offline protected mapped opener can
recover a valid guard by restoring the identified original pair, syncing each
rename, validating identities again, then removing the guard last. Recovery can
itself be interrupted at every move. Unknown, partial, damaged or inconsistent
records remain guarded with `REBUILD_INCOMPLETE`; they are never guessed empty.
Direct/legacy opens retain the existing conservative guard refusal. See
[rebuild recovery](rebuild-recovery.md).

After replacement, native data admission follows the live inode. While DMS is
still exclusive and the structural fence remains odd, both mapped envelopes are
invalidated and rewritten with a fresh authority UUID and the live identity,
retaining dimensions. The process block follows the new UUID, and the recovered
read version resets its publication epoch before readers can return. Canonical
path registry keys remain stable through the replacement; native admission still
validates physical identity and refuses alias namespaces. The participant's
password/collation settings follow a completed replacement, including rollback
which leaves the replacement live. Upgrade and AutoRebuild enter this same
maintenance scope only when actually replacing a file.

The last reference includes facades, reader generations, queued writer requests
and native holders. At zero references, the cache closes, bootstrap excludes new
attachers and an exclusive DMS conversion determines whether this is also the
last process. Only then may final close acquire main and WRITE, independently
recover/sync a healthy core, and checkpoint according to the persisted CHECKPOINT
rules (zero still disables automatic close checkpoints). A stopped/uncertain
fence is left for the next ordinary opener rather than published by disposal.
A live remote reader retains DMS and its exact WAL floor; writer disposal cannot
retire that mapping or truncate away its view. The remote last participant later
performs the final checkpoint.

Healthy final close disables admission and clears Ready while preserving valid
envelopes and dimensions for P2-G reuse. It releases WRITE after storage close,
then main, then its slot, mapping and DMS. The next first opener still validates
actual DB/WAL independently before publishing Ready. A kill at any close edge
leaves either a fenced recovery or a reusable retired mapping; neither admits
unvalidated storage. Managed I/O/flush failures preserve recovery evidence.

`SharedOneFileMaintenance*` and `SharedOneFileFinalClose*` cover sole/peer admission,
rekey, indexed payload preservation, encrypted files, installation and repeated
recovery death cuts, directory-sync errors, guard damage and warm remote readers.
The fault model is Linux x64 process death with surviving OS cache, injected I/O
failure and corrupt metadata. Directory barriers rely on qualified local
filesystem rename/fsync semantics; this is not device-reset or whole-path
modeled-power-loss qualification.
