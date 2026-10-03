# pbt-handle: transaction-handle access kind for the parallel property test

Historical adapter (fork [JKamsker/LiteDB#133](https://github.com/JKamsker/LiteDB/pull/133) trees only; capability `handle-api`). It adds the
`handle` access kind to the state-machine property test of
`LiteDB.Tests/Concurrency/ParallelProperty/` (M5, branch `safety/m5-models`):

| File | Content |
| --- | --- |
| `HandleAccessKind.cs` | `IAccessKind`: command generation, shrinking, dispatch |
| `HandleExecution.cs` | execution against `ILiteTransaction`, overlap detector, exception mapping |
| `HandleModel.cs` | the permitted-outcome model (`Apply`) |
| `HandleModel.Callback.cs` | model of a bulk insert whose input callback runs ordinary work |
| `HandleObservations.cs` | canonical observations and exception mapping |
| `HandleCampaign_Tests.cs` | time-boxed campaign driver for net proofs (trait `Category=NetProofCampaign`) |
| `register-handle-kind.patch` | the one-line registration in `AccessKinds.All` (apply with `git apply`) |
| `replay-overlay.patch` | what the replay freeze (`proof/pr133-nets-frozen`, commit `fa99fa9a4`) added besides the copy: counterexamples name the handle slot (`PropertyCommand.ToString`), and the broken-engine detection self-tests pin the upstream kinds, because handles refuse decorated engines (apply with `git apply`) |

The model was written only from `docs/transaction-handles.md` and the public API as
they exist at `e22281057` (identical at `d189f7a89` and `9d63ccc22`), plus a one-page
card of the handle API's public surface. It does not encode any defect. Its rules, with every
permissive choice marked, are in the XML documentation of `HandleModel`.

## Applying by hand

```
cp -r <harness>/LiteDB.Tests/Concurrency/ParallelProperty <tree>/LiteDB.Tests/Concurrency/
cp <harness>/tools/net-proofs/adapters/pbt-handle/*.cs <tree>/LiteDB.Tests/Concurrency/ParallelProperty/
git -C <tree> apply <harness>/tools/net-proofs/adapters/pbt-handle/register-handle-kind.patch
dotnet build <tree>/LiteDB.Tests/LiteDB.Tests.csproj -c Release -f net8.0 -p:TestingEnabled=true
```

With `net_proof.py`, list this directory in `net.overlay.adapters` and the patch in
`net.overlay.patches`. Patches are applied before adapters, so the ParallelProperty
directory must already be in the tree (an overlay commit). Capability probe for the
copied adapter: `pbt-handle-adapter` = file `LiteDB.Tests/Concurrency/ParallelProperty/HandleAccessKind.cs`.

## Running

```
LITEDB_PBT_ACCESS_KINDS=ordinary,legacy,handle dotnet test LiteDB.Tests -c Release -f net8.0 \
  -p:TestingEnabled=true --no-build --filter "FullyQualifiedName~ParallelProperty_Tests"

LITEDB_PBT_MODE=Direct LITEDB_PBT_MAX_SUFFIX_THREADS=4 LITEDB_PBT_TIME_BUDGET_S=300 \
LITEDB_PBT_BASE_SEED=1000 LITEDB_PBT_ARTIFACT_DIR=<dir> \
  dotnet test LiteDB.Tests -c Release -f net8.0 -p:TestingEnabled=true --no-build \
  --filter "FullyQualifiedName~HandleCampaign_Tests"
```

`LITEDB_PBT_EARLY_TIMEOUT_MS=<ms>` reports lock timeouts faster than that bound as
`LockTimeout:early`; the model then permits them only for a self-wait (the lock holder
is a handle the waiting thread itself executed calls on). Without it, LOCK_TIMEOUT is
permitted wherever a conflicting holder exists, regardless of how fast it came.

Command notation in counterexamples: `handle.Begin+lend1#3` begins handle 3 and lends
it in slot 1; `handle.Insert(c0, 2, p=7)#3` runs on handle 3; `#-1` runs on whatever
handle is lent in slot 1 (a handoff from another thread); no suffix is an ordinary
call made inside a handle unit.

## Second-attempt options (off by default)

Added after the first proof attempt stayed quiet (verification group V-pbt, see `docs/safety-net-retrospective.md`):

- `LITEDB_PBT_HANDLE_HANDOFF=dense`: generator only. Borrowed units make 1-3
  consecutive calls on the lent handle; blocks lend their handle more often and pause
  between operations. The first attempt formed few cross-thread calls on one handle.
- `LITEDB_PBT_SELF_WAIT_FAIL_FAST=1` (with `LITEDB_PBT_EARLY_TIMEOUT_MS`): a lock timeout
  whose holder is a handle that only the waiting thread ever executed on must be early.
  Source: the API card's refusal table (a collection write lock held by another owner on
  the same thread fails with LOCK_TIMEOUT immediately), not `docs/transaction-handles.md`
  at the handle commits. It was added knowing the ledger's description of row 12, so a
  proof using it is recorded as tuned, not as designed from the invariant.
- `LITEDB_PBT_HANDLE_CALLBACKS=1` (Direct only): some handle writes become
  `InsertBulk+cb:Op/cN/k#h`, a bulk insert on handle h whose input enumeration first
  runs an ordinary call on the same thread, while the handle executes and holds the
  target collection's lock. Source: the documentation's sentence that ordinary
  collections used in a mapper/input callback remain ordinary operations. The first two
  attempts had no shape where a handle is *executing* on the thread that waits. The
  statement has two points (lock taken and callback run; then the insert), so other
  threads may time out against the lock it holds in between. (A first, one-point
  version was too strict and produced a false failure; see the V-pbt verification in `docs/safety-net-retrospective.md`.)
- `LITEDB_PBT_HANDLE_LENT_CALLBACKS=1` (with callbacks): calls on a lent handle (another
  thread's handle, sequential handoff) may be callback commands too.
- `LITEDB_PBT_EXECUTING_SELF_WAIT_FAIL_FAST=1` (with `LITEDB_PBT_EARLY_TIMEOUT_MS`): a
  callback's ordinary write that conflicts with the *executing* handle must fail early.
  Source: the API card's normative rule 3, which is documentation text only from
  `e821ae74` on (after the handle commits under test). Proofs that use it are tuned.
