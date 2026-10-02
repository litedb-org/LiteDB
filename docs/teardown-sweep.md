# Teardown step-fault sweep

Every Dispose, Close, release or cleanup routine ("teardown path") must keep two promises
when one of its own steps fails: it hands the failure to its caller the way it declares
(propagate, return a failure list, record it on a primary error, suppress it behind the
primary, discard it), and it still releases everything else it owns
([storage ownership](rules/storage-ownership.md#buffers-and-cleanup): "a release path for
completion, early termination, exceptions, and repeated disposal"). The sweep checks both,
at every step of every registered path, under two failure models.

## Pieces

| Piece | Where |
| --- | --- |
| Registry attribute `[TeardownPath(name, declared, basis)]` | `LiteDB/Utils/TeardownPathAttribute.cs` |
| Step markers `TeardownSteps.Before/After`, `TryCatch.Step` | `LiteDB/Utils/TeardownSteps.cs`, `LiteDB/Utils/TryCatch.cs` |
| Scenario state (armed fault, visits) | `LiteDB/Utils/TeardownScenario.cs` |
| Step catalog (what fails, models, what a skip may leave) | `LiteDB.Tests/Safety/TeardownStepCatalog.cs` |
| Drivers (prior state + entry per path) | `LiteDB.Tests/Safety/TeardownDrivers*.cs` |
| Case runner and oracles | `LiteDB.Tests/Safety/TeardownSweep.cs`, `TeardownSweepPlan.cs`, `TeardownSweepRunner.cs` |
| Known findings | `LiteDB.Tests/Safety/TeardownKnownFindings.cs`, reproductions in `LiteDB.Tests/Safety/Tests/TeardownKnownFinding_Tests.cs` |
| xUnit sweep | `LiteDB.Tests/Safety/Tests/TeardownSweep_Tests.cs` |
| Fuzz target `teardown-faults` | `LiteDB.Fuzz/Targets/TeardownFaultsFuzzer.cs` |
| Fault-point registration (family `teardown-step`) | `.github/safety/fault-points.json` |

Everything in `LiteDB/` compiles to nothing outside `DEBUG || TESTING`: the attribute and the
markers are `[Conditional("DEBUG"), Conditional("TESTING")]`, so a production build keeps no
attribute usage in its metadata and no marker call or argument in its IL. The empty marker
methods and the attribute and enum types themselves remain, as M1's `Reachability` does.

## Declared dispositions

`TeardownDisposition` mirrors the oracle's `FaultDisposition` value for value (a fixture test
checks the mirror). A path declares the set its entry may give a fault raised inside it:

| Value | The entry ... |
| --- | --- |
| `Propagated` | throws the fault, or an exception whose cause chain carries it |
| `ReturnedAsFailureList` | returns normally and its returned failure list carries the fault |
| `RecordedAsCleanupError` | throws the primary error with the fault recorded on it |
| `Retried` | retried past the fault and completed normally |
| `SuppressedPreservingPrimary` | throws the caller's primary error unchanged |
| `Discarded` | returns normally and nothing reports the fault |

An observed disposition outside the declared set fails as `disposition:`; a fault replaced by
an unrelated exception (observed `None`, shown as `Replaced`) always fails. Declarations come
from the code AND the documentation; where they disagree the inventory says so, and a
contradiction is a finding, not a declaration. Upstream `LiteEngine.Dispose` declares
`Discarded`: it calls `Close()` and drops the returned list.

## Steps and the two injector models

A step is one action that can fail in reality (I/O, a callback, a wait that rethrows a holder's
failure). `TeardownSteps.Before(name)` and `After(name)` bracket it inside the block whose
handling a real failure of that action would get; `TryCatch.Step(name)` names the next
`TryCatch.Catch`, which reaches Before/After inside its own `try`. A case arms one fault:

- **skip**: thrown at `Before`, so the action never runs. Its own obligations stay undone by
  definition; the catalog lists them per step (`SkipLeaves`, as violation kinds) and only those
  are excused.
- **fail-inside**: thrown at `After`, so the action ran and then failed. Nothing is excused.

The plan described one marker per step boundary. A boundary inside a `finally` (for example
the lease release of `SharedDataReader.Dispose`) cannot stand for "the previous step failed
inside", so the markers are explicit Before/After pairs; the two models stay distinct. Step
names are literal `<Path>.<step>`; the owner path is the name without its last segment. Each
`Before` (and `TryCatch.Step`) counts the `fault-point:<name>` marker.

A skip excuses violation kinds, not individual resources: a skip that aborts the path also
excuses the later steps' leftovers of the same kind. The fail-inside case of the same step
propagates the same abort and excuses nothing, so such an omission is still reported there.

## Oracles (per case)

After the entry returned or threw, every scenario participant is stopped, then:
`reached` (FaultReached: the armed fault fired), `disposition` (FaultDisposed against the
declaration), `connection.*` (ConnectionClean for every connection the driver disposed),
`ownership.*` (latched Shared ownership violations of this scenario's connections),
`quiescent.*` (Quiescent at scenario end: handles, native mutex, reader registry, `-tmp`
scratch, LiteDB threads), `durable.*` (Durable on a cold Direct reopen against the case's
ledger), `scratch-live.*` (a live spilled reader's scratch exists, checked during the scenario)
and `leak.page-buffers` (page buffers finalized while in use). A baseline (unarmed) run must
be clean and must not throw or return failures. A baseline that reaches no step, or a step
without a catalog entry, fails the driver.

Each case runs on a fresh thread: a skipped release can leave a native mutex owned by, and a
thread-static scope count set on, the thread that ran it; both end with that thread.

## Scope and budget

| Scope | Cases from each driver's baseline |
| --- | --- |
| Default | the path's own steps, first and last occurrence, both models; one composition case per nested path (its first step reached, first occurrence, fail-inside where allowed); a path without own steps (it delegates its whole teardown, as `LiteEngine.Dispose` does) gets every nested step's first occurrence under both models |
| Full (`LITEDB_TEARDOWN_SWEEP=full`) | every occurrence of every step site reached, both models |

Cases run in parallel batches on separate files (8 Direct, 16 Shared: a Shared case mostly
waits about a second for idle holder threads to exit). Process-wide checks (LiteDB threads,
leaked page buffers) run once per batch; if one fires, the batch reruns one case at a time so
each violation is attributed. Skips that may leak page buffers by design form their own batch.
Measured on a 24-core Linux host under other load: Default 25 paths in about 55 s total, the
slowest path below 10 s (each test case is bounded by `TestCaseTimeout` 30 s).

## Inventory

`declared at` lines refer to the instrumented tree. Drivers are `<variant>/<mode>`.

| Path | Declared at | Declared | Basis | Own steps | Drivers |
| --- | --- | --- | --- | --- | --- |
| `LiteEngine.Close` | `LiteDB/Engine/LiteEngine.cs:211` | ReturnedAsFailureList | each step in `TryCatch`, list returned; callers decide | monitor, checkpoint, disk, sort-disk, locker | open-work/direct |
| `LiteEngine.CloseOnError` | `LiteDB/Engine/LiteEngine.cs:268` | ReturnedAsFailureList | `TryCatch` seeded with the causal error, list returned | monitor, mark-invalid, disk, sort-disk, locker | fatal/direct, invalid-datafile/direct |
| `LiteEngine.Dispose` | `LiteDB/Engine/LiteEngine.cs:343` | Discarded | calls `Close()` and drops its list (FOLLOWUP item 5) | none (delegates to Close) | open-work/direct |
| `LiteEngine.Rebuild` | `LiteDB/Engine/Engine/Rebuild.cs:20` | Discarded, Propagated | drops the old engine's `Close()` list; a failure building the replacement propagates ([rebuild recovery](rebuild-recovery.md)) | none | committed/direct |
| `EngineState.Stop` | `LiteDB/Engine/EngineState.cs:73` | SuppressedPreservingPrimary | `CompleteStop` drops `CloseOnError`'s list; the failing operation rethrows its own error | none | write-failure/direct |
| `TransactionMonitor.Dispose` | `LiteDB/Engine/Services/TransactionMonitor.cs:209` | Propagated | `TryCatch`, one `AggregateException` | transaction, slot, explicit-aborted | open-work/direct |
| `TransactionService.Dispose` | `LiteDB/Engine/Services/TransactionService.cs:463` | Propagated | page releases and reader attempted, `AggregateException` | snapshot-pages, snapshot-lock, reader | rollback/direct, auto-commit/direct |
| `DiskService.Dispose` | `LiteDB/Engine/Disk/DiskService.Dispose.cs:10` | Propagated | every action in `TryAction`, `AggregateException` | data-pool, log-pool, delete-log, cache | after-monitor/direct |
| `SortService.Dispose` | `LiteDB/Engine/Sort/SortService.cs:65` | Propagated | `TryCatch`, `AggregateException` | container, return-position, return-reader | spilled-reader/direct |
| `SortDisk.Dispose` | `LiteDB/Engine/Sort/SortDisk.cs:106` | Propagated | no handling; `LiteEngine.Close` collects it | pool, delete | after-spill/direct |
| `AesStream.Dispose` | `LiteDB/Engine/Disk/Streams/AesStream.cs:216` | Propagated | `TryCatch`, `AggregateException` | writer, reader, encryptor, decryptor, aes, stream | data-file/direct |
| `BsonDataReader.Dispose` | `LiteDB/Document/DataReader/BsonDataReader.cs:148` | Propagated | marks itself disposed, then the source's disposal propagates | source | partial/direct |
| `RebuildService.DiscardReplacement` | `LiteDB/Engine/Services/RebuildService.cs:347` | RecordedAsCleanupError | deletion failures go to the build failure's `Data[LiteDB.Rebuild.RollbackErrors]`, build failure rethrown ([rebuild recovery](rebuild-recovery.md)) | delete-data, delete-log | duplicate-key/direct |
| `LiteDatabase.Dispose` | `LiteDB/Client/Database/LiteDatabase.cs:415` | Propagated, Discarded | the checkpoint-override restore propagates; engine disposal is the engine's own path | checkpoint-override | stream/direct, file/direct, shared/shared |
| `SharedEngine.Dispose` | `LiteDB/Client/Shared/SharedEngine.cs:439` | Propagated, Discarded | unguarded steps propagate; core-close lists dropped (`CloseRetainedCore`); [shared-mode safety](shared-mode-safety.md): returns only after the release | retire-reads, wait-pin, early-handles, early-readers, handles, readers, coordination | open-work/shared, pin/shared, from-own-operation/shared |
| `SharedEngine.ClosePin` | `LiteDB/Client/Shared/SharedEngine.Readers.cs:159` | Propagated, Discarded | runs on the pin holder; a failure becomes the pin's error, rethrown by `WaitReleased`, dropped when nobody waits | coordination | dispose/shared |
| `SharedEngine.CheckpointAfterLastReader` | `LiteDB/Client/Shared/SharedEngine.Readers.cs:206` | Discarded, Propagated | I/O and access failures swallowed; database errors surface (code comments) | close-finally | last-reader/shared |
| `SharedEngine.CheckpointOnDispose` | `LiteDB/Client/Shared/SharedEngine.Readers.cs:257` | Discarded, Propagated | I/O, access and `LiteException` swallowed ("Dispose must not fail because of this cleanup"); others propagate | close-finally | idle-wal/shared |
| `SharedEngine.OnOwnerExited` | `LiteDB/Client/Shared/SharedEngine.cs:207` | Discarded | runs in `SharedMutexOwner`'s catch-all ("The next open recovers") | idle-handles | exited-owner/shared |
| `SharedEngine.OpenEngine` | `LiteDB/Client/Shared/SharedEngine.Coordination.cs:16` | SuppressedPreservingPrimary | a core whose publication failed is closed best effort and the original error rethrown ([teardown callbacks](shared-teardown-callbacks.md)) | none | publication-failure/shared (.NET 8+; not applicable on .NET Framework) |
| `SharedMutexPin.Hold` | `LiteDB/Client/Shared/SharedMutexPin.cs:206` | Propagated, Discarded | the close callback's failure becomes the pin's error; the mutex is released either way | close (fail-inside only) | reader-end/shared |
| `SharedMutexOwner.Exit` | `LiteDB/Client/Shared/SharedMutexOwner.cs:166` | Propagated | a scoped release failure propagates after the gate is reopened | scope-release | scoped-write/shared |
| `SharedMutexOwner.ReleaseAll` | `LiteDB/Client/Shared/SharedMutexOwner.cs:223` | Discarded | "Never throws": the holder's release failure is swallowed | send-release | foreign-transaction/shared |
| `SharedMutexOwner.ReleaseExitedOwner` | `LiteDB/Client/Shared/SharedMutexOwner.cs:487` | Discarded | the owner-exited cleanup's failure is swallowed; mutex and gate released after it | cleanup (fail-inside only) | exited-owner/shared |
| `SharedDataReader.Dispose` | `LiteDB/Client/Shared/SharedDataReader.cs:67` | Propagated, Discarded | the inner reader's and release callback's failures propagate; the core closes it triggers drop their lists | lease | leased/shared |

Platform-only steps (the coverage fact excuses them elsewhere; `faultPointGates` marks them
advisory for the Linux smoke): `SharedEngine.Dispose.handles`, `.early-handles` and
`SharedEngine.OnOwnerExited.idle-handles` run only where Shared mode caches file handles
(Windows, `SharedFileHandles.IsSupportedFor`).

Deliberately not registered:

- `SharedEngine.RejectAbandonedTransaction`: its orphan close is unreachable in-process
  (owner-exit and pin close always close the core first).
- The Coordinated engine: experimental, outside this sweep.
- `MemoryCache`, `StreamPool`, `SharedFileHandles`, `SharedReaderRegistry` Dispose: swept as the
  actions of their callers' steps (`DiskService.Dispose.cache`, `.data-pool`,
  `SharedEngine.Dispose.handles`, `.readers`).
- `SharedMutexOwner.ReleaseMutex`: a primitive release that throws only on a caller bug.

Code and documentation observations (gaps, not contradictions): `SharedEngine.Dispose` drops
core-close failure lists and `CheckpointOnDispose` swallows I/O and `LiteException`, while its
other steps propagate; [shared-mode safety](shared-mode-safety.md) promises that Dispose returns
only after the release but says nothing about cleanup failures. `LiteEngine.Rebuild` drops the
old engine's `Close()` list. The declarations record the code's behavior in both cases.

## Known findings (dev 7b71bc4d)

A case whose unexpected violations all match a registered finding (path, steps, model, mode,
violation kinds) is reported `known` and passes; a finding that no case of its path reproduces
any more fails the sweep ("no longer reproduces; remove it together with its fix"). Each has a
minimal reproduction in `TeardownKnownFinding_Tests` that passes while the defect exists; a
temporary try/finally fix makes each of them fail.

| Id | Paths | Defect | Since | Evidence |
| --- | --- | --- | --- | --- |
| `shared-dispose-aborts-remaining-cleanup` | SharedEngine.Dispose, ClosePin, CheckpointOnDispose, ReleaseAll; LiteDatabase.Dispose (Shared) | `SharedEngine.Dispose` sets `_disposed` and then runs its cleanup unguarded: a failing step (retire-reads, wait-pin rethrowing the holder's failure, the final checkpoint's scoped release, the reader registry, coordination) propagates and skips every later step; a second Dispose returns at once, so slot/lease handles and `-shared-live`/`-shared-state` stay open until process exit | #3003 (`3b9e579f1`) | class 1 (controlled fault at a named step) |
| `sortdisk-dispose-skips-scratch-delete` | LiteEngine.Close, CloseOnError, Dispose | `SortDisk.Dispose` has no try/finally: when closing the scratch streams fails, `-tmp` (the last spilled sort's keys) is never deleted | `c9eb2d9f2` (2019) | class 1 |
| `litedatabase-dispose-skips-engine-after-checkpoint-restore-failure` | LiteDatabase.Dispose (stream) | the checkpoint-override restore runs before `_engine.Dispose()` without try/finally: a non-fatal failure leaves the engine (streams, transactions, locks) open, and Dispose throws `INVALID_TRANSACTION_STATE` instead of the caller's error. Natural trigger, no injection: dispose a stream database while its thread still has an open transaction | #2652 (`bf3987fbc`) | class 1, natural reproduction |

All three contradict [storage ownership](rules/storage-ownership.md#buffers-and-cleanup); none is
fixed here (the safety net only reports).

## Fuzz target `teardown-faults`

Each step takes the next driver (round robin from a random offset, so 30 steps cover every
driver), draws a prior state within what the driver tolerates (document count, open reader and
pending transaction on other threads, spilled sort, upload, encryption, Shared peer), runs the
baseline and one random case from the Full plan. Outcomes go to `teardown.jsonl` and
`faults.jsonl`, never to `trace.jsonl`; the trace records only the choices. A known finding
passes unless `LITEDB_FUZZ_STRICT_KNOWN=1`; anything else fails as
`TEARDOWN_FAULTS_<PATH>_<KIND>`. Markers: `situation:teardown-faults-skip-fired`,
`situation:teardown-faults-fail-inside-fired`. Runs in the `linux-core-utc` smoke leg.
Evidence class: Direct cases are controlled (class 1); Shared cases involve holder threads
whose interleaving is native (class 2), so their records keep the full outcome.

## Adding a path or step

1. Put `[TeardownPath("<Type>.<Member>", <declared>, "<basis: code and docs>")]` on the method.
2. Bracket each failing action with `TeardownSteps.Before/After("<Path>.<step>")` (or
   `tc.Step(...)` before a `TryCatch.Catch`); pass `applies: false` when there is no action.
3. Add a catalog entry (what fails, models, what a skip leaves) and a driver in
   `TeardownDrivers.Direct.cs` or `.Shared.cs`. Without them the sweep fails: "new teardown path
   X has no sweep driver", "step X has no TeardownStepCatalog entry", "no driver reaches
   teardown step X".
4. Register the step in `.github/safety/fault-points.json` (family `teardown-step`).

Reading a failure: each line is `<path>/<variant>/<mode> <model> <step>#<occurrence> [prior]
fired= observed= thrown=` followed by its violations. Rerun one path with
`--filter "DisplayName~<path>"`; `LITEDB_TEARDOWN_SWEEP=full` widens it.

## Overlay: paths that exist only on another revision

A fork or historical tree can sweep its own teardown paths without changing these files: add
`[TeardownPath]` and step markers in that tree, then one file with the three partial methods:

```csharp
namespace LiteDB.Tests.Safety
{
    internal static partial class TeardownDrivers
    {
        static partial void Overlay(List<TeardownDriver> drivers) { /* drivers.Add(new TeardownDriver(...)); */ }
    }
    internal static partial class TeardownStepCatalog
    {
        static partial void OverlaySteps(List<TeardownStepInfo> steps) { /* steps.Add(...); */ }
    }
    internal static partial class TeardownKnownFindings
    {
        static partial void OverlayFindings(List<TeardownKnownFinding> findings) { /* ... */ }
    }
}
```
