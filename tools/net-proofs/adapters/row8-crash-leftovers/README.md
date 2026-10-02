# row8-crash-leftovers: crash-leftover prior and spilled-reader kind

Dimensions added for the row-8 proof (sort scratch left stale / deleted under a live reader) of
[JKamsker/LiteDB#133](https://github.com/JKamsker/LiteDB/pull/133), kept here so the recorded proofs can
be re-run. `row8-scratch.patch` is `git diff proof/pr133-nets-frozen proof/row8-scratch` and applies
unchanged to the replay of the fix (`proof/row8-scratch-fix`).

| Part | Files | Label |
| --- | --- | --- |
| Crash-leftover prior: a stale sort scratch (`-tmp`) next to the database before the scenario starts; forced on with `LITEDB_TEARDOWN_CRASH_LEFTOVERS=1`, drawn last in `teardown-faults` | `TeardownCase.cs`, `TeardownStates.cs`, `TeardownFaultsFuzzer.cs` | **tuned-after-fix**: chosen knowing the fix's subject line ("Restore stale sort cleanup ...") |
| Spilled-reader kind in `reader-handoff`, checked with `ScratchLive` while the reader is live; `LITEDB_EXPLORER_SCRATCH_SELFTEST=1` deletes the live scratch to show the check fires | `ReaderHandoffScenario.cs`, `ExplorerHost.cs`, `FuzzExplorerHost.cs`, `markers.json` | designed from the ledger's one-line description |
| `LITEDB_EXPLORER_KINDS` for the full-matrix test | `ConcurrencyExplorer_Tests.cs` | plumbing |

The judging oracle is the frozen `Quiescent` check (no `-tmp` scratch after every participant
stopped); nothing in the patch changes an oracle.

## Recorded use

Ledger entries `row8-crash-leftovers` (proven, level tuned-after-fix: 48 baselines report
`quiescent.scratch` at the known-bad replay, none at the fix) and `row8-spilled-reader` (not fired).

```bash
git -C <replay tree> apply tools/net-proofs/adapters/row8-crash-leftovers/row8-scratch.patch
dotnet build <tree>/LiteDB.sln -c Release -p:TestingEnabled=true
LITEDB_TEARDOWN_CRASH_LEFTOVERS=1 dotnet test <tree>/LiteDB.Tests -c Release -f net8.0 --no-build \
  --filter "FullyQualifiedName~LiteDB.Tests.Safety.Tests.TeardownSweep"
```
