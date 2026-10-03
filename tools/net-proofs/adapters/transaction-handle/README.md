# transaction-handle adapter (historical)

Access kind `handle` and two handle-only scenarios for the general concurrency explorer
(`docs/concurrency-explorer.md`). Upstream `dev` has no transaction-handle API, so nothing here
is part of any project: `net_proof.py` copies these files into
`LiteDB.Tests/NetProofAdapters/Handle` of a fork revision that has `BeginTransaction()` /
`ILiteTransaction` (capability `handle-api`), after the overlay brought in the explorer and the
M1 probes. The explorer discovers the access kind and the scenarios by reflection, so the copy
needs no edit elsewhere. On upstream the `handle` kind reports NOT APPLICABLE (never a pass).

| File | Content |
| --- | --- |
| `HandleAccess.cs` | `HandleAccess` (`handle`, capability `handle-api`, not thread-affine, transactional): every generic scenario (callback-pause, transaction-contention, reader-handoff with the reader bound to the unit, collection-cycle) runs with handles; `HandleChecks` (refusal and state oracles) |
| `HandleOverlapScenario.cs` | `handle-overlap`: overlapping read/Commit/Rollback on an executing handle refused and the handle stays Active; Shared same-file ordinary work refused inside a bound callback, other-file work succeeds; Commit refused with an open bound reader; reader handoff to another thread; completion on another thread; disposed reader refused (PR explorer schedules 0-11) |
| `HandleIndependenceScenario.cs` | `handle-independence`: two handles with pending writes, no uncommitted visibility to ordinary readers, either completion order, rollback of one (PR explorer schedules 12-23; Direct) |

Not ported: the PR's Shared admission schedules (they observe the fork-internal
`TransactionAdmission.Observe` hook) and its lifecycle schedules (fork-only `SessionLifetime` /
`OperationLifetime` hooks). The generic `close`/`rebuild`/`fatal` maintenance dimensions cover
close and rebuild against an active handle without those hooks.

Compile check (done once, not part of CI): a scratch worktree at fork commit `e28612aa5`, with
the explorer, its M1 dependencies (`LiteDB/Utils/Reachability.cs`,
`LiteDB/Client/Shared/SharedOwnershipEvents.cs`, `LiteDB.Tests/Safety/`) and these files copied
in, builds `LiteDB.Tests` for net8.0; see the M3 report for the exact adaptations.
