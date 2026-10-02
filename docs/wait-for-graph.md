# Wait-for graph (test builds)

`LiteDB/Utils/WaitGraph*.cs` is typed dependency instrumentation compiled only under
`DEBUG || TESTING`. Every blocking site in `LiteDB/` registers its wait before it blocks, and
every acquisition and release registers a hold. Before a thread blocks, the graph searches for a
cycle that leads back to that thread, classifies it, and **latches** the finding in a side
channel. It never throws through library code and never changes what a wait does: a deadlocked
test still hangs or times out as before, but the report says why, within milliseconds of the
wait starting. A production build contains none of it
(`dotnet build LiteDB/LiteDB.csproj -c Release -p:TestingEnabled=false`).

## Model

- **Resource**: something a thread waits for, typed by its **primitive**: `Monitor`, `Lock`,
  `NamedMutex` (one resource per name, process-wide), `SemaphoreSlim`, `Ownership` (a Shared
  connection's logical ownership), `Gate`, `Lease`, `Pin`, `Handoff` (a command run by a helper
  thread), `Event`, `Condition` (`Monitor.Wait` on a predicate), `ThreadJoin`, `TaskWait`, `Barrier`.
- **Hold**: a resource held by an **owner**, recorded on the thread that acquired it. A
  *thread-affine* hold (a monitor, a directly owned OS mutex, a transaction lease keyed by its
  thread) ends only by that thread's progress. Any other hold (a Shared connection's ownership,
  a pin) ends by its owner's progress, which runs on the recorded thread only while that thread
  executes a **frame** of the owner: a public call, a read of a retaining reader, a pin operation.
  An owner without a frame is idle: another thread may end it (a reader disposed elsewhere, a pin
  ending for a waiter), so waiting for it is not a deadlock. A teardown frame *claims* every hold
  of its owner on any thread, because those holds end only after the teardown returns.
- **Wait (edge)**: a thread blocked on one or two resources, typed by **bound** (`unbounded`,
  `timeout(value)`, `cancellation`) and **origin** (`library`, or `driver` for test-driver
  callbacks, joins and barriers). A cycle is *bounded* only by a wait that ends without progress of
  the cycle's own threads: a timeout, or a cancellation that has already been requested. A
  cancellation nobody has requested yet (`WaitBound.CancelledBy(token)` with the token not
  cancelled, or `WaitBound.Cancellation` when the site does not pass its token) is not a bound for
  classification. Whether it ever comes is the application's choice (a close, a cancel), so until
  then the cycle hangs exactly like an unbounded one. A token linked to a timeout (`CancelAfter`) is
  a timeout; declare it with `WaitBound.After`. A wait *via handoff* is performed by a helper thread for the
  registering thread (a holder thread acquiring the OS mutex for its caller).
- **Recursion**: `Monitor`, `Lock`, `NamedMutex` and `Ownership` let their owning thread enter
  again without waiting. A direct same-thread wait on a thread-affine hold of such a primitive is
  valid recursion, not an edge. Via a handoff it is not recursion: the helper thread never gets a
  mutex the caller holds.

Graph holds are recorded after the real acquisition and removed before the real release, so the
graph never shows a hold that does not exist. A search reads per-node versions and accepts a cycle
only if no hold, wait or frame it followed was removed meanwhile; an unstable search is retried and
then given up. The graph may miss a cycle; it must not invent one.

## Classification and failure rules

| Rule id | Finding | Default | Proof (known-bad fires, fix quiet, unchanged suite quiet) |
| --- | --- | --- | --- |
| `self-wait` | The holder executes on the waiting thread (length 1), and the edge is not a recursion the primitive grants | **fails** | Row 12 (PR #133 `e2228105`, fix `d189f7a8`): a handle's mapper callback inserts into the collection its own handle holds; the owner-keyed collection lock no longer refuses the same thread, so the callback waits for itself until `TIMEOUT`. 4/4 failing cases latch `self-wait` at 51-161 ms; the fix's 6 cases latch nothing; the 2 cases where the lock belongs to an *idle* handle another thread completes latch nothing. Row 16 (dev `5dd942a7`, fix `265c2497`): 28/28 hanging peer-callback cases latch `self-wait`. Level: reproduction (the fixes' own tests), not generic. |
| `unbounded-cycle` | A cycle across threads that none of its waits ends by itself: each is unbounded or only cancellable, with no cancellation requested | **fails** | Row 13 (PR #133 `cb36c346`, fix `0d5e5eff`): a raw close queues while an active read's callback waits, without a bound, for fresh work on another thread; the close fences that work. Latched as `unbounded-cycle` (length 3: fenced fresh op, close draining leases, callback joining the fresh op) at 29-184 ms; quiet at the fix. Level: reproduction, and only with the callback's wait registered as a driver edge (`WaitGraph.Join`): the fix's own test waits with `Task.Wait`, which the graph cannot see. Cancellable waits joined the rule later: a cycle through an unrequested cancellation used to be `bounded-cycle`, which hid real hangs on the PR #133 trees (a handle callback calling its own Shared connection waits on the owner gate, typed with the session's closing token, which no one closes; V-E2 `69b0663a0`). The dev tree has no cancellable site, so the change is quiet on the unchanged suite. |
| `bounded-cycle` | A cycle across threads with a wait that ends by itself (a timeout, or a cancellation already requested) | reports | Never a default. LiteDB resolves crossed collection locks by a lock timeout (latched with correct outcomes by the suite's permitted-history and site tests), and on the PR #133 trees closing a database whose own thread still holds a transaction resolves by a 10 ms exclusive try (latched by existing tests that pass). Row 3 (PR #133 `e28612aa`, raw close vs a collection-lock waiter) latches here, because the waiter's wait is bounded by `TIMEOUT`. |
| `lock-order` | Lock order A then B on one thread, B then A seen on another thread, no active cycle | reports (advisory) | Never a default: a Shared pin's holder re-enters its connection with a non-blocking try while it owns the OS mutex (`named-mutex / shared-ownership`), on every tree. |

A harness fails on the **failing rules**: by default `WaitGraph.DefaultFailing` (`self-wait`,
`unbounded-cycle`). `LITEDB_WAITGRAPH_FAIL` replaces the set: rule ids, `default`, `all`, or `none`
(report only); `WaitGraph.SetFailing` changes it at run time. At the end of the test or scenario the
harness raises `DeadlockDetectedException` (`WaitGraph.ThrowIfFailing`). Library code never throws
it, and the wait itself is never disturbed: a test that deadlocks still hangs or times out, and then
also fails with the cycle printed.

A rule joins the default set only with a proof: it fired at a known-bad commit, stayed quiet at the
fix, and stayed quiet across the full `LiteDB.Tests` net8.0 suite on unchanged `dev` with the rule
failing. Proof records: `.github/safety/net-proofs.json` (PR #133 rows run through
`tools/net-proofs/adapters/waitgraph-pr133`). A finding that turns out to be wrong is a defect of the
site's typing (primitive, bound, owner) and is fixed there, never by removing the rule.

The graph keeps no thread and no owner alive: a thread is referenced weakly (a finished thread
keeps its execution context and `AsyncLocal` values), the lock-order history keeps thread ids, and a
thread's list of held resources is weak, so a hold that is never released (an abandoned transaction)
stays reachable only through its primitive, as the real lock does
(`WaitGraphRetention_Tests`).

Findings are deduplicated per rule, signature and context (`WaitGraph.Context`, the running test),
with a count, the milliseconds from the start of the context to the first detection (`atMs`) and
how long the detecting thread had waited (`waitedMs`).

Environment:

- `LITEDB_WAITGRAPH=0` disables recording (diagnosis only; on by default).
- `LITEDB_WAITGRAPH_REPORT=<path>` appends every finding as a JSON line (`rule`, `failing`,
  `signature`, `context`, `atMs`, `waitedMs`, `pid`, `text`). Child processes inherit it.
- `LITEDB_WAITGRAPH_FAIL=<rule ids>|default|all|none` sets the failing rules (unset: `default`,
  that is `self-wait,unbounded-cycle`).

`LiteDB.Tests` applies `[assembly: WaitGraphCheck]`: it sets the context to the running test,
prints the findings latched during it, and fails it for a finding of a failing rule. A test that provokes
findings on purpose takes them with `WaitGraph.TakeFindings()`. `LiteDB.Fuzz` writes the findings
of a run to `waitgraph.txt` in its run directory (outside the hashed trace) and fails the run as
`WAIT_FOR_CYCLE` for a finding of a failing rule.

## Reading a finding

```text
[self-wait] x1 at 3 ms
Wait-for cycle, self-wait: the holder executes on the waiting thread (length 1):
  thread 'worker' #14 waits (unbounded, library, via a helper thread) at SharedMutexOwner.Enter (via holder) for SharedEngine#7 on named-mutex '<name>'#12 [NamedMutex]
    held by SharedEngine#6 (acquired at SharedMutexOwner (via holder)), executing on thread 'worker' #14
  frames (innermost first):
    thread 'worker' #14: SharedEngine#7 < SharedEngine#6
```

Each step names the waiting thread, the wait's bound and origin, the site, the owner it waits for,
the resource and its primitive; then the owner holding it, where it was acquired, and the thread
executing that owner. The frames show why that thread executes it: here connection #7's call runs
inside connection #6's call (a user callback), and #6 holds the mutex #7 waits for. Owner numbers
are per-process identities.

## Adding a blocking site

A new blocking site must register with the graph (see
[storage and ownership](rules/storage-ownership.md)). Each hook is one call inside
`#if DEBUG || TESTING` (or a `[Conditional("DEBUG"), Conditional("TESTING")]` helper); keep a
`WaitGraph.Resource` field on the object that owns the primitive.

```csharp
#if DEBUG || TESTING
private readonly WaitGraph.Resource _graph = WaitGraph.Create("my-gate", WaitPrimitive.SemaphoreSlim);
#endif

#if DEBUG || TESTING
using (WaitGraph.Wait(_graph, WaitBound.After(timeout), "MyGate.Enter", owner))   // before blocking
#endif
_semaphore.Wait(timeout);
#if DEBUG || TESTING
WaitGraph.Acquired(_graph);                       // thread-affine hold of this thread
WaitGraph.Acquired(_graph, connection);           // hold of an owner that executes in frames
WaitGraph.Released(_graph, ownerOrThread);        // before the real release, from any thread
WaitGraph.Enter(connection); /* ... */ WaitGraph.Exit(connection);   // the owner executes here
WaitGraph.Recheck();                              // each iteration of a poll loop
#endif
```

Register a wait only where the thread actually blocks, after any recursion check, and dispose the
scope as soon as the blocking call returns. A thread that blocks on behalf of another (a holder
thread acquiring for its caller) is represented by the caller's wait on the real resource, with
`viaHandoff: true`. Test drivers add their own edges: `WaitGraph.DriverWait(resource, bound, site)`
for a callback or barrier waiting on an event another thread owes (that thread calls
`WaitGraph.Acquired(resource)` until it signals), and `WaitGraph.Join(thread, bound, site)` for a
cross-thread join.

## Instrumented sites (dev)

| Site | Resource (primitive) | Bound |
| --- | --- | --- |
| `TransactionGate.TryEnterReadLock` / `TryEnterWriteLock` (all `LockService` and `TransactionMonitor` admission) | `transaction-gate` leases / writers (`Gate`) | timeout `TIMEOUT` |
| `CollectionLock.TryEnter` | `collection-lock '<name>'` per engine (`Monitor`, `Lock` on .NET 9+) | timeout `TIMEOUT`, only when contended |
| `SharedMutexOwner.Enter` gate poll | `shared-ownership` of the connection (`Ownership`) | unbounded, rechecked per poll |
| `SharedMutexOwner.TakeGate` (via holder), `TakeDirect` and `SharedMutexScope.Take` through the turnstile | `named-mutex '<name>'` (`NamedMutex`) | unbounded |
| `SharedMutexOwner.Send` handoff (other than an acquisition), `WaitForRelease` | `shared-holder-command` (`Handoff`), `shared-release-in-flight` (`Event`) | unbounded |
| `SharedMutexTurnstile.Wait` | `named-mutex '<name>.Turn'`, then the shared mutex (`NamedMutex`) | unbounded |
| `SharedMutexPin.Acquire` (via holder), `WaitReleased` | `named-mutex`, `shared-pin-end` (`Pin`) | unbounded |
| `SharedEngine.WaitForMutexWaiters` | `shared-mutex-waiters` (`Condition`) | unbounded |
| `SharedEngine.WaitForAdmittedCalls` | `shared-admitted-calls` (`Condition`, own calls excluded) | timeout 10 s |
| `SharedEngine.TryEnterForDispose` | ownership and `named-mutex` | timeout 2 s (poll, rechecked) |

Frames: `SharedEngine.Call` (every public call), `SharedEngine.Dispose` (a public call; its
close block claims all holds), `SharedCallFrames.Enter` (retaining reads and disposal, ownership
frames; teardown frames claim all holds), `SharedDataReader.GraphOwner` (a retaining reader's
reads and disposal execute the hold it keeps: its pin, else the connection), and `SharedMutexPin`
operations. Holder threads take over the holds they release while they run an exited owner's
cleanup or a pin's close. On a tree without `SharedCallFrames`, set `SharedDataReader.GraphOwner`
wherever `SharedEngine` creates a reader.

## Not instrumented, and why

| Site | Reason |
| --- | --- |
| `SharedMutexOwner.Run` `_posted.Wait(Poll)`, `SharedMutexPin.WaitForEnd` `_signal.WaitOne(Poll)` | Helper threads idling for commands or for the owner's operations to drain; nobody waits for them while idle, and what waits for them is expressed by the command, pin and mutex holds |
| `SharedMutexPin.Acquire` `holder.Join()` | Only after a failed acquisition, for a holder that already finished |
| `SharedMutexTurnstile.TryWait` / `HasWaiter`, `SharedMutexOwner.TryEnter` gate `Wait(0)` | Non-blocking |
| `SharedEngine.CreateEngine`, `SharedCoordinationFile.RetrySharingViolation` `Thread.Sleep` | Bounded retries of a Windows sharing violation; they wait for the OS, not an owner |
| `SharedEngine.CoordinatedReads` `Thread.Sleep(delay)` | Read pacing behind a writer; a delay, not a wait for a resource |
| `MemoryCache` page-loading `Monitor.Wait` (`GetReadablePage`, `GetWritablePage`) | Waits for the thread loading the page, whose factory only performs I/O; a hold per cache miss would cost more than it can find. Gap if a factory ever waits for a graph resource |
| `WalIndexService` `_indexLock` (`ReaderWriterLockSlim`), checkpoint `Monitor.Enter(commitLock)` and `WalWriterLock` | Engine-internal leaf locks in the order commit lock, index lock, WAL writer; not held across any registered wait. Gap: an inversion among them would hang without a finding |
| Plain `lock` statements (`SharedEngine._useLock`, `_snapshotGate`, `_waitersLock`, `SharedMutexOwner._send`/`_sync`, `MemoryCache._sync`, `HeaderPage`, disk stream locks, caches) | Short critical sections; monitors are reentrant, so a same-thread callback does not wait. Gap: `_useLock` also guards engine open and close, which can run caller streams; a cross-thread cycle through it would hang without a finding |
| `IOExceptionExtensions.WaitIfLocked` `Task.Delay(n).Wait()` | A sleep before retrying a locked file |
| `EntityMapper` `WaitHandle.WaitOne(5 s)` | Waits for a caller-supplied initialization token, bounded |
| `Client/Coordinated/*` (`CoordinatorGate`, `CoordinatorMutex`, `CoordinatorSignals`, `CoordinatorHost`, `CoordinatorProtocol`, `CoordinatorStatusPage`, `CoordinatedEngine`) | Experimental coordinator: its waits depend on peer processes and IPC the in-process graph cannot see. Gap |

`GroupByPipe` `item.Result` and the `string.Join` matches of a primitive grep are not waits.

## Sites that arrive with explicit transaction handles

When the handle PR merges, register its blocking sites with the typing the historical overlay
`tools/net-proofs/adapters/waitgraph-pr133` uses on the PR #133 trees (table in its README):
`OperationLifetime.Enter/Exclusive` (operation leases per thread, the exclusive slot, and the
maintenance fence a queued close/rebuild holds for fresh work), `SessionLifetime.Close` (session
leases, close worker, final release), the per-name writer `SemaphoreSlim` and the
`TransactionHolder` open/close/job events, the owner-keyed `CollectionLock` (a `Condition` recursive
per owner key, never per thread: thread-owned transactions hold it thread-affinely, an explicit
transaction through its `TransactionContext`, whose bound-call scope is a frame that claims all its
holds), and the owner-keyed `TransactionGate` leases. Waits that the session's closing token can
cancel should pass it (`WaitBound.CancelledBy(closing)`, not the overlay's token-less
`WaitBound.Cancellation`), so a cycle that a close has already started to cancel classifies as
bounded.
