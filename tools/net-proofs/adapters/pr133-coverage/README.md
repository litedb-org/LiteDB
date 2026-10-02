# pr133-coverage: coverage correction for the JKamsker/LiteDB#133 replay

A harness configuration for net proofs on `proof/pr133-replay` trees, not a net change. It adds
only registrations; no oracle, deadline, generator choice, fail rule or random draw changes. Name it
in every proof record as

    overlay.adaptations: "pr133-coverage patch (base-revision known finding #3071 + PR exception-contract declarations)"
    independence: designed-from-invariant; coverage correction added after first pass

It is based on a DIFFERENT known defect (#3071, fixed on dev by #3072 `265c2497`) and on the PR's
own documents, never on a ledger row's fix.

## Why

First pass (verification group V-A, see `docs/safety-net-retrospective.md`): on PR trees before `c6e6c848f` (the merge that
brings #3072 into the PR) most explorer seeds stop within a few steps on the #3071 hang: a callback
running inside a Shared call that keeps the native mutex calls another connection to the same file,
which waits forever. A hang ends the fuzz process (the Deadline oracle captures stacks and exits),
so known-finding matching cannot continue past it: the explorer's mechanism for hangs is generation
exclusion. And chaos-maintenance declares dev's close contract, which the PR changed on purpose, so
every close scenario stopped on `NOT_PERMITTED_*`.

## What it registers

| Part | File | Active when | Effect |
| --- | --- | --- | --- |
| (a) known finding `base-shared-peer-call-waits-for-own-outer-ownership` | `LiteDB.Tests/Engine/ConcurrencyExplorer/ExplorerKnownFindings.Pr133Coverage.cs` (+ `partial` and `.Concat(BaseRevisionFindings())` in `ExplorerKnownFindings.cs`) | the tree lacks `LiteDB.Client.Shared.SharedCallFrames` (the #3072 refusal; present on dev since `265c2497`, in the replay from `c6e6c848f` = `bf52b6d8a`) | transaction-interleavings: Shared `callback=peer` vectors of `callback-pause`, `transaction-contention`, `reader-handoff` are excluded (traced with `excludedBy`, counted `excludedByKnownFinding`); fingerprint `DEADLINE_*@<those scenarios or lifetime-chaos>@mode=shared@...@callback=peer` |
| (a) same finding, lifetime-chaos | `LifetimeChaosProgram.KnownHang` (one line) | same probe | a Shared node body `peer` is withheld (replaced by `same-connection`, listed as `withheld ... (base-shared-peer-call-waits-for-own-outer-ownership)` in the program text, counted `withheldByKnownFinding`). The random stream is unchanged, so every other choice of the program stays the same |
| (b) PR close contract | `LiteDB.Fuzz/Targets/ChaosMaintenancePr133Contract.cs` (+ two hooks in `ChaosMaintenanceDeclarations.cs`) | the tree has the PR's `LiteDB.SessionLifetime` | close scenarios only (rebuild/fatal keep dev's sets): see the table below |

(b) is derived only from `docs/transaction-handles.md` at `39f6c6b0` (the sentences are unchanged
through `dace941d1`):

- S1 "Disposal moves a session from Open through Closing to Closed. It rejects new work, cancels
  pending handle admission, settles idle owned handles, and drains executing work before releasing
  its engine lease."
- S2 "Each disposal call waits up to 10 seconds, including cleanup time. A timeout leaves the session
  Closing with needed resources retained; cleanup continues automatically when outstanding work
  finishes. A retry joins cleanup and reports any deferred cleanup failure once."
- S3 "Using a disposed `LiteDatabase` now throws `ObjectDisposedException` naming `LiteDatabase`,
  instead of `LiteException` with `ENGINE_DISPOSED`. [...] This is an intentional exception-contract
  change; it does not make concurrent use of a closing facade valid."

- S4 "Already-open ordinary readers retain their existing independent lifetime; bound readers belong
  to their handle/session."

S2 does not name the timeout's type; the type is the PR's own `TimeoutException` at `39f6c6b0`
(`SessionLifetime.Close`: "Session close is still draining active work").

| Call (close scenarios, Direct and Shared) | dev declaration | PR declaration | Sentence |
| --- | --- | --- | --- |
| maintenance `Dispose`, `SecondDispose` | ok | ok, threw:TimeoutException | S2 |
| active, maintenance first (every call) | Direct: threw:ENGINE_DISPOSED; Shared: refused:ObjectDisposedException | refused:ObjectDisposedException | S1, S3 |
| active first, the call paused at its forced point (bulk, checkpoint, rebuild, commit paused in its WAL write) | Direct: ENGINE_DISPOSED / ObjectDisposedException (rebuild also ok); Shared: ok | ok (drained) | S1 |
| active first, an ordinary reader paused between rows | Direct: ENGINE_DISPOSED / ObjectDisposedException; Shared: ok | dev's set (unchanged) | S4 |
| active first, later calls of an idle explicit transaction (`TransactionUpsert`, `Commit` not in WAL, `Rollback`) | Direct: ENGINE_DISPOSED; Shared: refused (Rollback ok) | refused:ObjectDisposedException | S1 (settled), S3 |
| active first, before the forced point | ok | ok | - |

## Revisions

- `d43b38691` (v1): declared `ok` for an ordinary reader paused under an active-first close. Wrong
  reading of the documents: S4 keeps the reader's existing lifetime. Observed at the Direct trees as
  `NOT_PERMITTED_READER` (`threw:ENGINE_DISPOSED`) identically at K' and F' (V-A2 campaigns).
- v2 (this commit): the reader case falls back to dev's declaration (S4). Explorer part unchanged.

## Caveat: it masks the #3071 class where #3072 is missing

Part (a) withholds exactly the Shared `callback=peer` shapes that the merge of #3072 into the PR
(`c6e6c848f`, ledger row 27) fixes. With the patch on both sides that pair is identical per seed; the
recorded proof for row 27 ran the known-bad side without the patch (`knownBadException` in the ledger
entries). Any proof whose defect belongs to the #3071 class must do the same.

## Applying

`net_proof.py` copies this directory to the inert `net-proof-pr133-coverage/` directory of a tree;
the patch itself is applied with `git apply`. On any replay commit (the patched files do not change on `proof/pr133-replay` after the freeze):

```bash
git -C <tree> cherry-pick <proof/pr133-coverage tip>      # code + this directory; or:
git -C <tree> apply tools/net-proofs/adapters/pr133-coverage/pr133-coverage.patch
dotnet build <tree>/LiteDB.sln -c Release -p:TestingEnabled=true -m:6
```

Both probes are evaluated at run time, so one patch serves every replay commit: before `c6e6c848f`
both parts are active; from `c6e6c848f` on only (b) is (the #3071 entry is not in the list at all,
so it can neither exclude nor match). Self-check (registrations only, before and after `c6e6c848f`):
a reflection probe (kept with the V-A2 artifacts, not in the repository) dumps the finding list, 1200
lifetime-chaos programs, 8000 explorer vectors with their exclusion and every chaos-maintenance
declaration; diff a patched and an unpatched build of the same commit.
