# Concurrency explorer

`LiteDB.Tests/Engine/ConcurrencyExplorer/` (namespace `LiteDB.ConcurrencyTesting`) drives real
LiteDB connections from dedicated actor threads through schedules that a controller forces, and
judges every operation against the outcomes its situation permits. It is the general form of the
interleaving explorer first written for the transaction-handle work (fork PR #133, `b0e1a8bb`):
the scenarios, dimensions and oracles below name library situations, not a particular defect.
The same code runs as xUnit tests and, linked into `LiteDB.Fuzz`, as the fuzz targets
`transaction-interleavings` and `lifetime-chaos`.

## Evidence classes

| Source | Class | What a failure carries | Replay |
| --- | --- | --- | --- |
| Forced schedules (`transaction-interleavings`, the xUnit theory) | 1 | the vector and the controller's recorded decisions | rerun the vector: the same decisions must give the same failure id |
| Dependency programs (`lifetime-chaos`) | 2 | the program text, the history, the environment | rerun `seed:step`; a non-reproduction is classified (schedule-dependent, environment-dependent, harness nondeterminism) and the finding is kept |

A forced schedule fixes the order of the edges the controller decides (who starts, who is
paused at a boundary, who is released when); it does not fix OS scheduling inside an operation.
That is why class 1 compares the failure id, not timings.

## Model

- **Actors** (`ExplorerSchedule.Actor`): one dedicated thread each (`concurrency-explorer-<name>`),
  draining a queue of operations. A is the actor whose operation is under test; B, C, D contend;
  E takes sequential handoffs; programs add `N0..N5` and `M`.
- **Operations** run through the host under the deadline their scenario **declares**
  (`ExplorerSchedule.LockBound` = max(3 x TIMEOUT, 15 s) for lock-bound operations with TIMEOUT 5 s;
  `Extended` = 60 s for operations whose callback is held at a boundary, rebuild, close and
  checkpoint). The clock starts when the operation starts on its actor. A late completion still
  fails (`DEADLINE_<OP>`).
- **Boundaries** pause an actor inside user code (a lazy input sequence, a `ReadTransform`, an
  upload stream, an input sequence's `finally`). `Hit` blocks the actor until the controller
  releases it; `Let` is a recorded release decision.
- **Decisions** (invoke, await, wait, release) are recorded in order and written to
  `explorer.history` and to the failure artifact.
- **Judging**: every finished operation is judged against a `Permit` set stated by the scenario
  from what races the operation: `Disposed` (ObjectDisposedException or ENGINE_DISPOSED),
  `Refusal` (InvalidOperationException, NotSupportedException, INVALID_TRANSACTION_STATE, or any
  LiteException thrown while a `refusal:*` reachability marker fired on the calling thread: M1's
  refusal rule), `LockTimeout` (a bounded wait gave up), `Fatal` (an injected I/O fault),
  `OtherLite`. A type no library contract produces fails as `UNEXPECTED_EXCEPTION_<TYPE>`; a
  permitted type in the wrong situation fails as `EXPLORER_UNPERMITTED_<OP>_<KIND>`. A failed
  operation no scenario judged is itself an error.
- **Model** (`ExplorerModel`): an independent record per database file of what was acknowledged
  (an auto-commit operation returned, or a unit's `Commit` returned true) and what is uncertain
  (the operation raced a close or a fault). Nothing is read back from the implementation until the
  cold check.

## Oracles

| Oracle (M1) | Where |
| --- | --- |
| Deadline | every actor operation, declared per operation class |
| Ownership | after every judged operation, for every live Shared connection; latched release violations surface at the next check |
| ConnectionClean | after every dispose (maintenance, callback, cleanup) |
| Durable + exact cold check | after all actors stopped: two cold reopens; exact ids and values (uncertain ids may take any of their possible values), byte-exact payloads, index catalog, indexed lookup = scan, files, untouched sentinel |
| Quiescent | at scenario end, for every file the run used |
| FaultReached / FaultDisposed | the fatal maintenance: a write that returned must have reached the armed WAL write; the injected fault must propagate |

`ExplorerLocalHost` (xUnit) calls the probes in `LiteDB.Tests/Safety/` directly.
`FuzzExplorerHost` (`LiteDB.Fuzz/Targets`) routes the same checks through `FuzzOracles`, so
explorer runs write `outcomes.jsonl`, `connection-clean.jsonl`, `quiescent.jsonl`, `faults.jsonl`
and fail with target-qualified ids; an overdue operation is reported by the runner's watchdog
(`deadline-failure.json`, `waitgraph.txt`) and the process exits.

Every run ends by flushing finalizers, so a page buffer a run leaked or released twice is
finalized before the next run (a buffer still held by background work may surface one run later).
The fuzz targets observe the TESTING hook `PageBuffer.FinalizedInUse` and fail that run with
`EXPLORER_PAGE_BUFFER_FINALIZED_IN_USE` instead of letting the finalizer's ENSURE end the process.

The explorer adds **driver edges** to the wait-for graph (docs/wait-for-graph.md) by reflection,
so it also compiles on trees without the graph: a boundary is owed by the controller until
released, an actor owes its running operation until it finishes, a callback awaiting another
actor's operation (`Dependency`), the controller awaiting an actor, and joins. The harness's own
coordination (boundaries, controller awaits, joins) is registered with the explorer's deadline
(`ControllerBound`, which ends it with `EXPLORER_UNRELEASED_BOUNDARY` or
`EXPLORER_CONTROLLER_TIMEOUT`), so a cycle through it, such as the controller awaiting an actor that
still waits at a boundary the controller owes, is a non-failing `bounded-cycle`. A `Dependency`
stands for the application's own wait and stays unbounded, so a library cycle through it fails. The
graph only reports; its findings for a failed run are written to `waitgraph.txt`.

## Dimensions and access kinds

`ExplorerConfiguration` (signature `mode=..;access=..;maintenance=..;callback=..;process=..;encrypted=..`):

| Dimension | Values |
| --- | --- |
| mode | `direct`, `shared` |
| access | discovered `IExplorerAccess` kinds: `ordinary` (auto-commit), `legacy` (`BeginTrans`/`Commit`/`Rollback`, thread-affine), `handle` only where the historical adapter is compiled in |
| maintenance | `none` (a checkpoint contends instead), `close` (Dispose of the connection), `rebuild`, `fatal` (an injected WAL write failure through the registered `SimulateDiskWriteFail` hook) |
| callback | what a user callback does: `none`, `same-connection` (a read and a write on its own connection), `peer` (a write on another connection to the same file, same thread), `other-file`, `dispose` (Dispose of its own connection) |
| process | `single`, `external-writer` (another process writes the file: Shared only) |
| encrypted | `false`, `true` |

Points outside a mode's contract are **not applicable**, never passed: `direct` + `peer` (a
Direct connection owns its file; Windows refuses a second one by file sharing, Unix does not
enforce `FileShare.Read`, and a second Direct writer in one process corrupted the WAL in a
survey), `direct` + `external-writer`, scenario-specific rules below, and access kinds this
revision lacks. The `handle` kind needs capability `handle-api`; on upstream it reports
NOT APPLICABLE naming the capability (`ExplorerAccessKinds.NotApplicableReason`), and a test
asserts that it never counts as a pass.

## Scenarios (class 1)

| Scenario | Variants | Situation | Permitted beyond success |
| --- | --- | --- | --- |
| `callback-pause` | 4 callback points x 6 orders x release early/late | A's operation is paused inside its callback (input, ReadTransform, upload stream, input teardown) while a read (or the external writer), the maintenance (or a checkpoint) and a write start in one of six orders; A completes its unit before contenders are awaited | what the maintenance races; the nested callback call may be refused or time out; a write behind a rebuild may fail disposed and must succeed on retry (Issue2965); after Dispose returned, a new operation is refused |
| `reader-handoff` | 6 orders x close on E or A x enumerator/data reader | a reader opened on A, advanced on B (its ReadTransform runs the callback dimension), closed on E or A after B returned | the maintenance; Rebuild may be refused (open readers) or time out. Yielded documents equal the snapshot at open. Thread-affine kinds are not applicable |
| `transaction-contention` | 6 orders x same/other collection x same/second connection | two units of work, A paused in its input callback holding its unit | the maintenance; B may time out (LOCK_TIMEOUT) and roll back |
| `collection-cycle` | 2 orders | Direct, transactional: opposite collection locks | at least one LOCK_TIMEOUT loser; winners commit (the graph reports a bounded cycle, allowed when outcomes are correct) |

## Dependency programs: `lifetime-chaos` (class 2)

`LifetimeChaosProgram.Generate(seed, step, kinds, includeKnown)` builds a program from
`(seed, step)` only: mode, access kind, encryption; 2-6 nodes (operations `InsertInput`,
`FindTransform`, `Upload`, `Read`, `Write`, `Checkpoint`), each on its own actor; 1-3 roots;
other nodes are children of a callback node at depth < 3, whose callback starts them (in parallel
or one by one) and waits for each; leaf callbacks do `none`, `same-connection`, `peer`,
`other-file` or `dispose`; one maintenance action (close, rebuild, fatal, or none) on a node's
connection, triggered from a node's callback, after a 0-40 ms delay, or after every node finished.

Awaited operations are independent of their waiter when nothing disturbs them, so every
dependency must complete: a Shared waiter holds its file's writer ownership, so Shared children
run on the next file (a callback waiting for work on its own file is the documented callback
limitation); Direct children write their own collection on the same file or run on the next
file, and an Upload child never shares its parent's file (the file storage collections). A
Direct checkpoint waits for exclusive access, so operations on its file may time out.
Permitted outcomes follow from what disturbs a node's connection (close, a dispose body) or file
(rebuild, fatal).

## Known findings

`ExplorerKnownFindings` registers defects the explorer reaches on this revision. Each entry has
a fingerprint regex over `FAILURE_ID@scenario@mode=..@access=..@maintenance=..@callback=..`, an
optional message regex, and evidence. Discovery continues past them: xUnit reports instead of
failing; the fuzz targets append them to `known-findings.jsonl` with the retained step directory.
Only a finding that would **hang** an actor or **crash** the process is also excluded by a
precise vector predicate (or, for the stale lock-timeout pragma, avoided by the fixture) (and `lifetime-chaos` withholds the matching generation choices, listing
them in the program text); exclusions are traced and counted, never silent.
`LITEDB_EXPLORER_INCLUDE_KNOWN=1` runs the excluded vectors too.

| Id | Class | Exclusion |
| --- | --- | --- |
| `direct-dispose-under-active-operation` | crash: Direct Dispose (or a fatal stop) under another thread's active operation returns at once; the operation fails with an internal ENSURE (LiteException 999) and leaked page buffers fail the PageBuffer finalizer ENSURE, which kills a TESTING process | Direct + close; Direct + callback dispose; Direct + fatal with A paused in its input teardown |
| `direct-fatal-stop-during-upload-releases-page-buffers-twice` | crash: a fatal WAL write failure while `FileStorage.Upload` is paused in its source stream; the upload fails ("page buffer ownership was transferred to disk ... could not be rolled back") and page buffers end at share count -1, so the TESTING finalizer ENSURE kills the process | Direct + fatal, callback-pause upload point |
| `shared-close-leaks-page-buffer` | class 2: after a Shared close raced a paused callback, a page buffer became garbage with share count 1 (not reproduced in isolation) | none (the fuzz targets' audit records it; xUnit would crash on it) |
| `shared-dispose-from-input-teardown` | crash class: Shared auto-commit insert whose input sequence's `finally` disposes the connection fails with the same internal ENSURE | callback-pause, input-teardown point, Shared, non-transactional, callback dispose |
| `shared-peer-call-inside-own-explicit-transaction` | hang: inside an explicit transaction a call on another connection to the same file on the same thread waits for the writer ownership its own thread holds. With `BeginTrans` it is the mechanism of open issue #3073 (explicit transactions count as idle owners, which #3072 does not refuse); with `FileStorage.Upload`, which opens its own transaction and reads the caller's source stream under it, the callback hangs where docs/shared-mode-safety.md promises a refusal for user code inside a call | Shared + peer callback with a transactional access kind, or the upload point |
| `lock-timeout-pragma-stale-after-wal-restore` | 60 s lock waits: a connection that opens while TIMEOUT lives only in the WAL waits on locks with the data file's value (default 1 min) and ignores later `Timeout` changes (`LiteEngine.Open` builds `LockService` before the WAL restore replaces the header) | none: the fixture checkpoints its TIMEOUT and checks the lock service's effective value; include-known mode skips the checkpoint |

## Running it

```
# xUnit (net8.0): representative vectors, lifetime-chaos seed 2947 steps 1-16, the capability test
dotnet test LiteDB.Tests -c Release -f net8.0 -p:TestingEnabled=true --settings tests.runsettings --filter FullyQualifiedName~ConcurrencyExplorer_Tests
# the whole matrix, optionally narrowed (regex over the vector text) and sampled; report as TSV
LITEDB_EXPLORER_FULL=1 LITEDB_EXPLORER_FILTER='mode=shared' LITEDB_EXPLORER_SAMPLE=29 LITEDB_EXPLORER_REPORT=/tmp/survey.tsv dotnet test ... --filter FullyQualifiedName~Full_matrix_when_requested
# replay one vector / one program (finalizers are flushed afterwards, so a leaked-buffer crash is attributed)
LITEDB_EXPLORER_VECTOR='scenario=callback-pause;variant=14;seed=0;mode=shared;access=ordinary;maintenance=none;callback=peer;process=single;encrypted=false' dotnet test ... --filter FullyQualifiedName~ConcurrencyExplorerReplay
LITEDB_LIFETIME_CHAOS=2947:8 dotnet test ... --filter FullyQualifiedName~LifetimeChaosReplay
# fuzz targets
dotnet run --project LiteDB.Fuzz -c Release -f net8.0 --no-build -- --target transaction-interleavings,lifetime-chaos --seed 2947 --count 40 --artifact-dir artifacts_temp/explorer
```

The full xUnit matrix needs a run settings file without the 300 s session limit. CI runs the
representative set in the LiteDB.Tests jobs, the fuzz smoke leg `linux-concurrency-explorer`
(seed 2947, 40 steps each), and both targets in the nightly persistence shard.

`transaction-interleavings` visits the matrix with `ExplorerSelection.Rotating(seed, step, kinds)`:
a multiplicative permutation of the dimension points (so a short campaign spreads over every
dimension) with each point's (variant, seed bit) advancing per cycle; the visit is complete. Each
step runs one applicable vector: not-applicable and excluded vectors are traced and skipped.

## Extending it

- **A scenario**: implement `IExplorerScenario` (public parameterless constructor; discovery is
  by reflection). Make every decision from (variant, seed bit, configuration); judge every
  operation you start; account every write in the model; state the permitted outcomes in the
  class comment from what races each operation, never from a known defect.
- **An access kind**: implement `IExplorerAccess` (name, capability, thread affinity,
  transactional, `Begin`). A kind that needs library surface `dev` lacks goes into a
  historical adapter under `tools/net-proofs/adapters/<name>/`, copied into a fork tree by
  `net_proof.py`; see `tools/net-proofs/adapters/transaction-handle`.
- **A dimension value**: add it to the enum, give it a `DimensionRule` where a mode's contract
  excludes it, a contender or callback body, and a `Permit` rule.
- **A situation marker**: `Reachability.Sometimes("situation:explorer-...")` and an entry in
  `.github/safety/markers.json` (`check_reachability.py` scans this directory because LiteDB.Fuzz
  links it).
- **A known finding**: fingerprint plus message as narrow as the evidence; `Excludes` only for a
  hang or crash.
