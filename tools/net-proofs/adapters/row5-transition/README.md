# row5-transition: refused bound-object calls keep the handle Active

Historical adapter for fork trees of [JKamsker/LiteDB#133](https://github.com/JKamsker/LiteDB/pull/133)
(capability `handle-api`). It adds one explorer scenario, `handle-disposed-bound-object`
(`HandleDisposedBoundObjectScenario.cs`), next to the `transaction-handle` adapter's scenarios in
`LiteDB.Tests/NetProofAdapters/Handle`. Copy both adapters; the scenario uses `HandleChecks` and the
handle access kind from `transaction-handle`.

**Label: post-freeze, tuned-after-fix.** The check was added after the nets were frozen
(`proof/pr133-nets-frozen`). It implements review item 6 of the plan follow-up, which states the
transition a refused reader or enumerator operation must keep: the transaction stays Active and can
still commit its prior writes; then the cold Durable check. That item was written for this row by a
reviewer who had read the fix's regression test, so the ledger records the proof as tuned, even though
the scenario's author never read the fix. The scenario does not encode the defect: a late call may
succeed, report a disposed object or be refused; only the handle's state, the commit and the
acknowledged rows are judged.

## What it does

A's handle writes, opens a bound enumerator (`FindAll`) or data reader and reads one document. In the
overlap variants C tries to dispose that reader while A is paused inside another bound call (refused
before running). The closer (A, or B by sequential handoff) disposes the reader, then C uses it once
more. Afterwards: handle Active, B's Commit succeeds, prior writes survive a cold reopen.

## Recorded use

Branches `proof/row5-transition` (`b959846b2`, on the replay of `26406eca`) and
`proof/row5-transition-fix` (`29eb5d811`, on the replay of `9bf6137c`). Ledger entry
`row5-explorer-transition`: 32 of 48 vectors fail at the known-bad replay with
`EXPLORER_HANDLE_NOT_ACTIVE` (the late advance throws inside the guarded call and aborts the handle),
replaying 9/9 with the same id; 48/48 pass at the fix.

```bash
cp tools/net-proofs/adapters/transaction-handle/*.cs <tree>/LiteDB.Tests/NetProofAdapters/Handle/
cp tools/net-proofs/adapters/row5-transition/*.cs <tree>/LiteDB.Tests/NetProofAdapters/Handle/
dotnet build <tree>/LiteDB.sln -c Release -p:TestingEnabled=true
LITEDB_EXPLORER_KINDS=handle dotnet test <tree>/LiteDB.Tests -c Release -f net8.0 --no-build \
  --filter "FullyQualifiedName~ConcurrencyExplorer_Tests.Full_matrix_when_requested"
```
