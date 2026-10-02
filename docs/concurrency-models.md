# Concurrency models

Small, systematic models of LiteDB's lifetime state machines: admission of work
versus close, rebuild and fatal stop. They are explored with
[Microsoft.Coyote](https://microsoft.github.io/coyote/) 1.7.11 in the
`LiteDB.Tests.Concurrency` project (net8.0). A change to lifetime, admission,
locks or teardown models the affected state machine here first (see
[Adding a model](#adding-a-model-for-a-new-state-machine)).

A model is hand-written C# that mirrors the real code path step by step. It is
**class-1 evidence** (controlled schedules): Coyote records every scheduling
decision and every nondeterministic choice, and replaying the recorded trace
reproduces the same assertion. A model says nothing about code it does not
mirror. Model results and real-library results are kept apart: a model finding
is reported with its Coyote trace and, separately, with the outcome of a
real-code reproduction attempt.

## How a model works

- Every abstract thread runs a C# iterator. The code between two `yield return`
  statements is atomic; each yield is a point where Coyote may switch threads.
  Each step names the source location it stands for (`File.cs:line`), so the
  explored trace reads like the real code path.
- `Step.Wait(site, condition)` blocks without a bound (an untimed `Monitor.Wait`,
  `WaitOne()`, a user callback blocking on a task). `Step.TimedWait(site, ms, ...)`
  is a bounded wait: it expires only when no thread can make progress otherwise,
  earliest deadline first (discrete time; ties are a controlled choice). Real
  timeouts are long compared to the modeled work, so a bounded wait never
  expires while other work can still release it.
- Each Coyote iteration builds a scenario with controlled choices: 3 to 5
  abstract threads, one or two worker operations, one or two maintenance
  operations, helper threads where an operation needs one.

### Alphabet

| Kind | Meaning |
| --- | --- |
| `Fresh` | Top-level work on a thread that has nothing admitted |
| `Continuation` | Work that continues an admitted unit later or on another thread (a reader handed over and disposed) |
| `Nested` | Work started on a thread inside its own admitted work (same-thread recursion) |
| `Owner` | A transaction owner: `BeginTrans`, work, `Commit` across several calls of one thread |
| `CallbackDependency` | An active operation whose callback (lazy input) waits for fresh work started on another thread |
| `Rebuild`, `Close`, `Fatal` | Maintenance: rebuild, `Dispose`, a commit that fails with an I/O error (fatal stop) |

### Properties

The properties are stated without reference to any particular state machine;
the model reports what the code does at the corresponding line and
`LifetimeLedger` decides.

| Property | Checked by |
| --- | --- |
| Every operation completes or is rejected (liveness) | `LifetimeLivenessMonitor`: hot while an operation is pending; the world reports a model in which no thread can progress and no bounded wait is left, with every blocked thread's source location |
| No fresh admission after close acquired | `LifetimeLedger.Admitted`: the first admission of top-level work into a scope (`connection`, or an engine generation) whose close took effect |
| No owner rejected while its transaction is active | `LifetimeLedger.End`: an owner (or nested work inside an open unit of its thread) refused at an admission point while its unit could still complete and no close or fatal stop has doomed it |
| Close terminates once active work returns | The world: a close or rebuild that cannot finish although no admitted operation is still active |

"Close acquired" is where the real close takes effect: for the Direct engine the
registry close in `TransactionMonitor.Dispose`, or a `Dispose` call that returns
early because another close already set `_state.Disposed`; for a Shared
connection `Interlocked.Exchange(ref _disposed, 1)`. A unit is doomed when it
can no longer commit: Direct `_state.Disposed`/published failure, Shared
`_disposed` (`CompleteTransaction` refuses from then on).

## What is modeled

### Direct engine (`LifetimeModel/Direct`)

`LockService` with `TransactionGate` and `CollectionLock`, `TransactionMonitor`
with `TransactionRegistry` and the thread slot, `LiteEngine` auto-transactions,
explicit transactions, readers, `Close`, `Rebuild` (with `Open`) and the fatal
stop (`EngineState.Stop` with `Close(ex)`).

### Shared connection (`LifetimeModel/Shared`)

`SharedEngine` calls (`Call`, `AdmitLocked`, `EndAdmissions`), `OpenDatabase`
and `CloseDatabase` without pins, explicit transactions, a `FOR UPDATE` reader
streamed under the mutex and disposed on another thread, `Dispose` with its
bounded drain, `ReleaseAll`, final checkpoint attempt and `WaitForRelease`, and
`SharedMutexOwner` with its gate, recursion, generation, scoped (direct)
ownership and holder thread.

### Correspondence table

Each row maps a model transition (its site label) to the implementation state it
reads or writes. Paths are relative to `LiteDB/`.

| Model transition | Source | Implementation state |
| --- | --- | --- |
| `Transaction.cs:315` validate | `Engine/Engine/Transaction.cs` `ExecuteAutoTransaction`; `Engine/EngineState.cs:375-380` | `_state._exception`, `_state.Disposed` |
| `TransactionMonitor.cs:52/62/72/76` | `Engine/Services/TransactionMonitor.cs` `GetTransaction` | `_disposed`, `_slot` |
| `TransactionGate.cs:228` read wait (pragma TIMEOUT) | `Engine/Services/TransactionGate.cs:221-237`, `LockService.cs:47-67` | `_writer`, `_waitingWriters`, `_readers[thread]`, `_readerCount` |
| `TransactionRegistry.cs:36` publish, `:40` re-check | `Engine/Services/TransactionRegistry.cs:19-49` | slot CAS, `_closed` |
| `TransactionRegistry.cs:53` close and drain | `TransactionMonitor.cs:208-220`, `TransactionRegistry.cs:51-63` | `_disposed`, `_closed`, transactions disposed (foreign collection locks stay held) |
| `Snapshot(write c): LockService.EnterLock` | `LockService.cs:80-88`, `CollectionLock.cs` | per-collection `Monitor` owner, pragma TIMEOUT |
| `Transaction.cs:348/349`, `:371/372` | `CommitAndReleaseTransaction`, `RollbackAndReleaseTransaction`; `TransactionService.cs:290` `ENSURE(Active)`; `TransactionMonitor.cs:121-152` | transaction state; the lease is released only when the registry removal succeeded |
| `Query.cs:19`, `QueryExecutor.cs:73`, `BsonDataReader.cs:107`, `QueryExecutor.cs:83` | reader open, read, dispose | query-only transaction and its lease, released from any thread |
| `LiteEngine.cs:213/215/220/225/235` | `Engine/LiteEngine.cs:211-238` `Close` | `_state.Disposed`, monitor, close checkpoint `TryEnterExclusive` (10 ms), `LockService.Dispose` |
| `TransactionGate.cs:264` exclusive wait | `TransactionGate.cs:253-277`; `LockService.EnterExclusive` (pragma) and `TryEnterExclusive` (10 ms) | `_waitingWriters`, `_writer` |
| `Rebuild.cs:35/36/38/47/66/68`, `LiteEngine.cs:94/157/175` | `Engine/Engine/Rebuild.cs:19-71`, `LiteEngine.Open` | exclusive lease kept, `Close()`, new `_state`, `_locker`, `_monitor` |
| `EngineState.cs:413/428`, `LiteEngine.cs:270/285` | `EngineState.Stop`, `LiteEngine.Close(ex, origin)` | `_exception`, `Disposed`, monitor, lock service |
| `SharedEngine.Calls.cs:64` / `:73` | `Client/Shared/SharedEngine.Calls.cs:61-75`, `:145-156` | `_admitted[thread]`, `_admittedCalls` |
| `SharedEngine.cs:110` → `SharedMutexOwner.cs:131` gate wait, `:74` direct take | `SharedEngine.Waiters.cs:293-325`, `SharedMutexOwner.cs:128-136`, `:70-84` | `_gate`, `_owner`, `_recursion`, `_scope.Owner` |
| `SharedMutexOwner.cs:338/350` send, `:308` post, holder `:366/:404/:432` | `SharedMutexOwner.cs:306-359`, `:361-436` | `_command`, `_done`, `_held`, `_released`, gate reopened after a posted release |
| `SharedEngine.cs:114` | `RejectAbandonedTransaction` `:310-327` | `_transactionRunning`, `_transactionThreadId` |
| `SharedEngine.cs:121` admit, open, count | `OpenDatabase` `:121-142`, `AdmitLocked` `Calls.cs:29-36` | `_disposed`, `_engine`, `_databaseUsers` |
| `SharedEngine.cs:191/209`, `SharedMutexOwner.cs:202` | `CloseDatabase` `:180-211`, `SharedMutexOwner.Exit` `:165-209` | users, engine close, recursion, generation, posted release |
| `SharedEngine.cs:244`, `:276/287/294/306` | `BeginTransCore`, `CompleteTransaction` `:269-308` | transaction running, `TryEnter`, admission |
| `SharedEngine.Query.cs:172`, `SharedDataReader.Dispose` | `QueryUnderMutex`, `CloseDatabase(use, hold, generation)` | ownership retained by generation, released from any thread |
| `SharedEngine.cs:444/446`, `Calls.cs:177` (10 s), `:471`, `:483`, `Readers.cs:273/275/253/264` (2 s), `:492` | `SharedEngine.Dispose(bool)` `:442-494`, `WaitForAdmittedCalls`, `ReleaseAll`, `CheckpointOnDispose`, `WaitForRelease` | `_disposed`, admitted calls, engine, ownership |

### Not modeled

Each gap is a place where a model would need extending before it can speak for
that code: Shared pins (`SharedMutexPin`), leased snapshot readers and
coordinated reads, peer connections and the `SharedCallFrames` refusals
(covered by the peer-callback tests), other processes and turnstile contention,
an exiting owner thread (abandonment), teardown callbacks through custom streams
or `ReadTransform`, auto checkpoints after commit, page buffers and memory
accounting, and the inner engine's own locks in Shared mode. Lost wake-ups are
outside the model: every state change re-evaluates every blocked condition.

## Findings on dev `7b71bc4d`

Two Direct-engine behaviours violate "no fresh admission after close acquired".
Both are pinned as `Known_finding_*` tests (they assert the model still reports
them) and excluded from the passing Direct run, which therefore runs one
maintenance operation at a time. No production code was changed.

1. **Dispose during Rebuild reopens the engine.** `Dispose` on another thread
   while `Rebuild` holds exclusive admission closes the engine and returns;
   `Rebuild`'s own `Close()` then returns early (`LiteEngine.cs:213`) and
   `Open()` (`Rebuild.cs:66`) reopens it. Real-code reproduction: the
   `SimulateAfterExclusiveAdmission` hook disposes from another thread; after
   `Dispose` returned and `Rebuild` finished, an `Insert` succeeds, a query sees
   both documents and two database file descriptors stay open (deterministic).
2. **A concurrent second Dispose returns before the close completes.**
   `LiteEngine.cs:213` returns as soon as another close set `_state.Disposed`.
   Real-code reproduction: with the first close paused in its checkpoint
   (`CheckpointStage` hook), the second `Dispose` returns after 0 ms while the
   data and log files are still open (deterministic). The model's further
   consequence, work admitted between `LiteEngine.cs:215` and `:220`, has no hook;
   a 10 000-trial real-thread stress run did not hit it (class-2 evidence,
   classified schedule-dependent; every late operation was refused with
   `ENGINE_DISPOSED`).

Incidental, outside the models' abstraction (page accounting is not modeled): in that
stress run a single `Dispose` racing an active `Insert` on an in-memory Direct engine
left page buffers whose share count was -1 when finalized (524 buffers over 10 000
trials; the TESTING-build finalizer check reports them). Native-thread evidence, kept
as a finding for follow-up.

The parallel property test found a Shared-mode defect: on one thread, `BeginTrans`, a write
that fails (for example a duplicate key, which rolls the explicit transaction back), `BeginTrans`
again and `Commit` all succeed, yet the idle thread keeps the connection's mutex. Every call of
another thread then waits until that thread exits. Each started `BeginTrans` retains one ownership
recursion (`SharedEngine.cs:244-252`), and the implicit rollback never returns the first one.
It reproduced in 20 of 20 replays and standalone; generation avoids it through
`KnownFindings.SharedRestartAfterAbortRetainsMutex`.

Documented limitation, pinned rather than excluded: a Shared callback that waits
for another thread's call on the same connection hangs
([shared mode safety](shared-mode-safety.md): "Waits that cross threads ... are
not detected"); `Callback_waiting_for_another_threads_call_hangs_as_documented`
asserts the model reports it. Scenarios with that dependency otherwise always
include a `Dispose`, whose bounded drain ends it.

## Running and replaying

```bash
dotnet test LiteDB.Tests.Concurrency -c Release
LITEDB_COYOTE_ITERATIONS=20000 dotnet test LiteDB.Tests.Concurrency -c Release   # campaign
LITEDB_COYOTE_SEED=7 dotnet test LiteDB.Tests.Concurrency -c Release              # other seed
LITEDB_COYOTE_REPLAY=artifacts_temp/coyote/<name>.trace dotnet test LiteDB.Tests.Concurrency -c Release --filter <test>
```

Each test runs 1000 iterations per strategy (random and Coyote's priority-based
strategy) with a fixed seed; the project takes about 25 s. The world serves step
requests in arrival order, so the random strategy rarely delays one thread for many
steps. Bugs that need a late operation (a close that starts only after another
operation is deep in its callback) are found by the priority-based strategy, which is
why both run. A violation writes
`<name>-seed<seed>-<strategy>.trace` (Coyote's reproducible trace) and `.txt`
(the property, every operation, every blocked thread, the model trace with
source locations, and Coyote's readable trace) to `artifacts_temp/coyote` or
`LITEDB_COYOTE_ARTIFACTS`; CI uploads them as `concurrency-models`.
`A_recorded_trace_replays_the_same_violation` checks that a replay reproduces the
identical assertion.

## Adding a model for a new state machine

1. Read the state machine and list its shared fields, its locks and waits
   (bounded or not, and the bound), and every point where it admits, refuses,
   releases or tears down.
2. Write the state as a plain class and each code path as an iterator under
   `LiteDB.Tests.Concurrency/LifetimeModel/<Area>/`. Put a
   `yield return Step.At("File.cs:line ...")` before every access to shared
   state that another thread can interleave with; model every wait with
   `Step.Wait` or `Step.TimedWait` and its real bound. Keep `lock` regions atomic.
3. Report to the ledger: `Begin` each operation with its alphabet kind,
   `Admitted` at the last admission check with its fence scopes, `FenceAcquired`
   where a close takes effect, `TransactionLive` and `DoomTransactions` for owned
   units, and `End` with completed, rejected (refused at admission) or failed.
4. Choose the scenario in `Build` with `Host.Choose`; never use another source of
   randomness, or replay breaks.
5. Add a passing test for both strategies, and one deliberately broken variant
   per property the model is meant to protect, each asserting which property
   fires. A model whose broken variants pass has no teeth.
6. Add the model's rows to the correspondence table and its gaps to "Not modeled".

When a modeled state machine changes, update its model in the same change.

## Parallel property test

`LiteDB.Tests/Concurrency/ParallelProperty` checks the real public collection API and per-thread
transactions (`BeginTrans`/`Commit`/`Rollback`) in Direct and Shared mode against one model of
collection contents, snapshots, collection locks, abort marks and the Shared mutex. Its core
(generator, model, checker, shrinker, runner) does not depend on xUnit.

- **Sequential property:** one thread; after every command its result and a full read-back (on
  the same thread, and on another thread for the committed state) must match the model.
- **Parallel property:** a sequential prefix, then 2-3 suffixes on real threads released
  together. The observed history must be a *permitted history*: some order of the operations
  that keeps each thread's program order and real-time order explains every result and the final
  contents. The permitted set is listed with source references on `PermittedHistoryChecker`:
  ordinary (automatic) versus bound (explicit transaction) access, refusals, rollback (explicit,
  and after a failed operation inside a transaction, [explicit transactions](explicit-transactions.md)),
  timed-out losers (only with a conflicting holder; their transaction is rolled back), and
  uncertain outcomes (an interrupted call has its whole effect or none). This is not a
  linearizability claim for the library in general.
- After each case a probe writes every collection from another thread while the workers are
  still alive, so retained locks or mutex ownership surface as probe failures.

Run it with
`dotnet test LiteDB.Tests -c Release -f net8.0 -p:TestingEnabled=true --settings tests.runsettings --filter "FullyQualifiedName~ParallelProperty"`.
`LITEDB_PBT_ITERATIONS` raises the case count for campaigns (`LITEDB_PBT_BASE_SEED` picks the
first seed), `LITEDB_PBT_SEED` regenerates one case, and failure evidence goes to
`LITEDB_PBT_ARTIFACT_DIR`. A fuzz driver calls `ParallelPropertyRunner.Run(seed, count, mode, log)`;
a driver that records its own history checks it with `PermittedHistoryChecker.Check`.

**Evidence class.** The seed regenerates a case's inputs exactly; the thread schedule is the
operating system's, so the parallel property is native-thread evidence. A failure keeps the
inputs, the observed history, the synchronization boundaries and the environment, is replayed
(20 times by default), shrunk, and classified as reproducible, schedule-dependent,
environment-dependent or harness nondeterminism. A failure that does not recur is still a finding.
The checker's own controls run in CI: deliberately broken engines (a lost committed write; an
update exposing an intermediate state to concurrent readers) must be rejected, hand-written
impossible histories rejected, and a real crossed-transaction lock timeout accepted.

**Known findings** are excluded from generation by one named rule each in `KnownFindings`;
`LITEDB_PBT_INCLUDE_KNOWN_FINDINGS=1` generates them again. Remove the rule with the fix.

**Adding an access kind** (for example transaction handles): implement `IAccessKind` in one file:
name, capability, generation of self-contained units, shrinking, execution against
`ThreadContext`, and model transitions through `ModelState`/`DataOperations.Apply`, with owner
keys outside the thread range for transactions that do not belong to a thread. Then add it to
`AccessKinds.All`. Select kinds with `LITEDB_PBT_ACCESS_KINDS`. A kind whose library surface
is missing from the build makes the run NOT APPLICABLE: it is reported as such, never skipped
silently, and never counted as passing.
