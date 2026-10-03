# waitgraph-pr133 adapter (historical overlay)

The wait-for graph (`docs/wait-for-graph.md`) ported to JKamsker/LiteDB#133 (`JKamsker/LiteDB`,
`codex/transaction-handles`) trees, with the blocking sites that exist only on that branch
instrumented. It lets `net_proof.py` re-run the wait-for graph proofs of ledger rows 3, 4, 12 and
13 (ledger entries `row{3,4,12,13}-wait-for-graph` in `.github/safety/net-proofs.json`).
Nothing here is compiled by this repository.

## Files

| File | What |
| --- | --- |
| `graph-new-files.patch` | New files only, identical for every tree: `LiteDB/Utils/WaitGraph*.cs`, `DeadlockDetectedException.cs`, `SharedMutexOwner.WaitGraph.cs`, `LiteDB.Tests/Utils/WaitGraph{CheckAttribute,_Tests,Sites_Tests}.cs`. Graph core = `safety/m2-waitgraph` plus the two retention fixes of `safety/m5b-waitgraph-rules` (no default failing set: the run script passes `LITEDB_WAITGRAPH_FAIL`). |
| `sites-<commit>.patch` | Changes to existing files for exactly that commit (plain `git apply`): the dev sites (hand-ported where the PR rewrote them) and the PR-only sites below. One per known-bad and fix commit: `70429cb67` (row 4 bad), `e28612aa5` (row 3 bad = row 4 fix), `2124767a7` (row 3 fix), `cb36c346e` (row 13 bad), `0d5e5effa` (row 13 fix), `e22281057` (row 12 bad), `d189f7a89` (row 12 fix). |
| `run.sh` | Applies both patches, builds `LiteDB.Tests` (net8.0), runs a mode's tests with `LITEDB_WAITGRAPH_REPORT`, prints `WAIT_FOR_CYCLE:<rule>` per finding of a failing rule and exits 1. Test failures alone never fire. |
| `repro/*.cs` | Reproduction probes (not generic nets): row 4 parameterless begin under the caller's own reader/pin; row 13 callback waiting for fresh work, with the callback's wait registered as a driver edge (`WaitGraph.Join`). |

Modes: `generic` (`TransactionHandle|Shared` tests present in the tree), `repro-row3`, `repro-row4`,
`repro-row12`, `repro-row13` (the fix commit's own test, copied from the fix when the tree lacks it,
plus the probe). `WAITGRAPH_FAIL` (default `self-wait,unbounded-cycle`), `WAITGRAPH_FILTER`.

## PR-only sites (typed as on the branch)

| Site | Primitive / bound | Holds |
| --- | --- | --- |
| `OperationLifetime.Enter` | `Gate` (exclusive slot) + `maintenance-fence` when no exclusive owner yet; unbounded | operation lease per thread (`Lease`, released by its thread) |
| `OperationLifetime.Exclusive` (close, rebuild) | leases + exclusive slot, poll; timeout when given, else unbounded; `dependenciesDrained()` not modelled (a miss, never an invented edge) | exclusive slot (thread that runs close/rebuild/deferred close); fence while queued |
| `SessionLifetime.Close` | `Condition`, timeout 10 s | session leases (per thread), the close worker while it runs `RequestClose`, the thread running the final release |
| `TransactionAdmission.WaitLocal` (per-name writer `SemaphoreSlim`) | `SemaphoreSlim`; timeout, else cancellation (caller or session close), else unbounded | the `TransactionHolder` (its `Run` frame claims it) |
| `TransactionAdmission.EnterNative`, `SharedEngine.EnterOwner` closing poll | ownership + named mutex, poll; same bound | (dev ownership holds) |
| `TransactionHolder.Open` `_opened.Wait()` | `Event`, unbounded | the holder until it signals |
| `TransactionHolder.Run` `_close.Wait()` | `Event`, unbounded | the handle's `TransactionContext` (executes in bound-call frames; an idle handle has no executor) |
| `TransactionHolder.Release` `_done` | `TaskWait`, unbounded | the holder until its job ends |
| `CollectionLock.TryEnter` (owner-keyed rewrite) | `Condition` (recursive per owner key, never per thread); timeout = pragma `TIMEOUT` | thread-owned transaction: thread-affine; explicit transaction: its `TransactionContext` |
| `TransactionGate` read leases keyed by owner | `Gate` | thread owner: thread-affine; explicit owner: its `TransactionContext` |
| `TransactionContext.Scope` | frame of the transaction that claims all its holds (a bound call runs it; overlapping calls are refused) | – |
| `DirectEnginePool.Open` (5 s), admission registry mutex (5 s) | `Condition` / `NamedMutex`, timeout | opening thread / mutex owner |

Not instrumented: `SessionCloseScheduler` / `SharedHolderScheduler` idle waits (workers waiting for
work; nothing waits for them while idle). The `Shared` begin (`OpenTransactionResources`) is not a
frame of the connection: an experiment adding that frame after reading the row 4 fix's test is kept
on branch `proof/vwg-row4-frame-experiment` (not part of this overlay).

## Ledger entries (suggested)

Add a capability `waitgraph-pr133-adapter` (file `net-proof-waitgraph-pr133/run.sh`) and, per row:
`requires: ["handle-api", "waitgraph-pr133-adapter"]`, `overlay.adapters:
["tools/net-proofs/adapters/waitgraph-pr133"]`, `build: []`, `command: ["bash",
"{tree}/net-proof-waitgraph-pr133/run.sh", "{tree}", "{artifacts}", "repro-row12"]`,
`timeoutSeconds: 900`, `expect.knownBad.match: "WAIT_FOR_CYCLE:(?P<id>[a-z-]+)"`.
