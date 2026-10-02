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
  callbacks, joins and barriers). A wait *via handoff* is performed by a helper thread for the
  registering thread (a holder thread acquiring the OS mutex for its caller).
- **Recursion**: `Monitor`, `Lock`, `NamedMutex` and `Ownership` let their owning thread enter
  again without waiting. A direct same-thread wait on a thread-affine hold of such a primitive is
  valid recursion, not an edge. Via a handoff it is not recursion: the helper thread never gets a
  mutex the caller holds.

Graph holds are recorded after the real acquisition and removed before the real release, so the
graph never shows a hold that does not exist. A search reads per-node versions and accepts a cycle
only if no hold, wait or frame it followed was removed meanwhile; an unstable search is retried and
then given up. The graph may miss a cycle; it must not invent one.

## Classification (reported, not enforced)

| Rule id | Finding | Meaning |
| --- | --- | --- |
| `self-wait` | The holder executes on the waiting thread (length 1), and the edge is not a recursion the primitive grants | Candidate failure: no other thread's progress can end it (noted when the wait is bounded) |
| `unbounded-cycle` | A cycle across threads whose every wait is unbounded | Candidate failure |
| `bounded-cycle` | A cycle across threads with a bounded wait | Allowed when the outcomes are correct: a lock timeout is LiteDB's documented resolution (crossed collection locks) |
| `lock-order` | Lock order A then B on one thread, B then A seen on another thread, no active cycle | Advisory |

Every rule **reports** by default. A harness may switch a rule to **fail**
(`LITEDB_WAITGRAPH_FAIL=self-wait,unbounded-cycle` or `all`, or `WaitGraph.SetFailing`); then the
harness, at the end of the test or scenario, raises `DeadlockDetectedException`
(`WaitGraph.ThrowIfFailing`). Library code never throws it. Failure rules are enabled one at a
time, each proven on a known-bad commit.

Findings are deduplicated per rule, signature and context (`WaitGraph.Context`, the running test),
with a count, the milliseconds from the start of the context to the first detection (`atMs`) and
how long the detecting thread had waited (`waitedMs`).

Environment:

- `LITEDB_WAITGRAPH=0` disables recording (diagnosis only; on by default).
- `LITEDB_WAITGRAPH_REPORT=<path>` appends every finding as a JSON line (`rule`, `failing`,
  `signature`, `context`, `atMs`, `waitedMs`, `pid`, `text`). Child processes inherit it.
- `LITEDB_WAITGRAPH_FAIL=<rule ids>` makes harnesses fail on those rules (none by default).

`LiteDB.Tests` applies `[assembly: WaitGraphCheck]`: it sets the context to the running test,
prints the findings latched during it, and fails it only for a failing rule. A test that provokes
findings on purpose takes them with `WaitGraph.TakeFindings()`. `LiteDB.Fuzz` writes the findings
of a run to `waitgraph.txt` in its run directory (outside the hashed trace) and fails the run as
`WAIT_FOR_CYCLE` only for a failing rule.

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

When the handle PR merges, register: `OperationLifetime.Enter/Exclusive` (wait on the lifetime,
held by each operation and the exclusive holder), `SessionLifetime.Close` (wait until operations
drain), `TransactionHolder._opened.Wait` (wait on the holder thread's open, held by that thread),
and the per-name writer semaphore in `SharedEngine.Transactions` (wait and hold per name). Each is
a `WaitGraph.Wait` around the blocking call and `Acquired`/`Released` where the count changes.
