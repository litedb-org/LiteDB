# Implementing a critical change safely

Use this for every change CI labels `critical` (lifetime, ownership, locks, waits,
teardown, durability, exception contracts, public API, or the safety machinery itself).
It is the single procedure for every agent and person; Claude Code loads it as the
project skill `implement-safely` (`.claude/skills/implement-safely/`), whose prompt
templates any agent can use.

The brainstorm is not the safety net. Reviewers and subagents anticipating side effects
miss what nobody thought of; their output only counts once it becomes an invariant, a
reachability marker, a fuzz dimension or a manifest entry that runs on every PR.

**What is mandatory.** An obligation below is **MANDATORY** only when its net has a
recorded proof (see [Proven obligations](#proven-obligations): it fired at a real
known-bad commit and stayed quiet at the fix) or when it is pure bookkeeping that a CI
check enforces. A proof that exists only for a net tuned after reading the fix, only at
reproduction level (the fix's own test) or only on a model does not make a net mandatory
by itself. Everything else is **advisory** or a **pilot**, with the
reason stated; do it when it applies, report what it found, and never present it as proof.

## Critical changes

`python .github/scripts/classify_critical.py --base <merge-base>` prints the rules a
diff matches. A change is critical when it touches `LiteDB/Engine/Services/`,
`LiteDB/Client/Shared/`, `LiteDB/Client/Transactions/` or `LiteDB/Client/Direct/`; changes
a Dispose/Close/Release/finally, WAL/checkpoint/flush or public declaration line; adds a
`throw` or removes a `catch` (exception-contract widening); changes an `ILite*` member
or adds a public type; matches a lock/teardown/transaction/shared/fault/public-API
obligation of `.github/safety/fuzz-obligations.json`; or changes safety machinery
(oracles, markers, registries, corpus expectations, proof entries, scripts, workflows,
test settings, this template).

The **Critical label** workflow applies `critical` from the diff with the base's rules
and never runs PR code. The label never gates a merge by itself (the merge gate is
`build-and-test / Safety evidence`, see [safety evidence](safety-evidence.md#candidate-tree-validation)).
It selects heavier tiers: every fuzz target in the PR-selected job, mutation on the diff,
and the required [critical PR sections](#the-pr-description). Removing it by hand on a
critical diff re-applies it with a comment; change the diff or the rules instead.

## Evidence classes and not-applicable

| Class | Nets | A firing counts when |
| --- | --- | --- |
| 1 controlled | explorer forced schedules (`transaction-interleavings`), Coyote models, teardown step faults | the recorded decisions replay to the same assertion |
| 2 native | `lifetime-chaos`, `chaos-maintenance`, `shared-contention`, PBT schedules, Coyote partially controlled / `--no-repro` | inputs, history, boundaries and environment are retained; a real failure stays a finding even when a replay does not reproduce it |
| 3 performance | contended acquire, shared-slot workloads | repeated paired runs exceed the stated tolerance |

Classify a non-reproduction (schedule-dependent, environment-dependent, harness
nondeterminism) before concluding anything. A net, access kind or proof whose capability
a revision lacks (`net_proof.py capabilities --rev <rev>`, e.g. `handle-api`) reports
**not applicable** with the missing names: it is listed, never skipped silently, and
never counted as passing. A timeout is never weakened to make a net pass, and a flaky net
is a defect in the net.

## Phases

Phases 1-3 happen before the first production line; 4-5 during; 6 before review.

1. **Blast radius** (main agent). Run `classify_critical.py`, `check_contracts.py --base`
   (implicated contracts), `check_reachability.py --base` (declared markers, missing
   `api:` markers) and `select_fuzz_targets.py --base` (obligations, targets). For every
   method whose contract widens (starts throwing, throws a new type, stops catching,
   blocks where it did not), list every caller (grep or Roslyn `FindCallersAsync`) and
   decide whether it is safe under the new contract. Write a scratch `safety-plan.md`
   outside the tree; its caller list becomes the PR's *Blast radius*. MANDATORY
   (bookkeeping).
2. **Adversarial lenses** (4-6 isolated subagents, [prompt](../../.claude/skills/implement-safely/prompts/lens.md)):
   lifetime/teardown, concurrency and waits, contracts and callers, failure model and
   fault points, hot paths and wait primitives, prior art (`LiteDB.Fuzz/Corpus/`,
   `docs/*review*.md`, closed issues). Each returns dimensions, invariants, manifest
   claims, markers and scenarios; the main agent merges them into phase 3. Advisory:
   no recorded proof; useful only through what phase 3 encodes.
3. **Executable spec, committed before production code.** The user-facing doc; every
   normative sentence anchored or claimed; the `api:`/`situation:` markers registered;
   the target or dimension that drives them; manifest entries for intended behavior
   changes. The reachability gate is red here by design. MANDATORY (bookkeeping).
4. **Implement in slices.** Each slice keeps the fuzz smoke green, every declared marker
   hit and every differential difference claimed by the manifest. New subsystems add
   dimensions, not standalone tests.
5. **Findings and fixes.** Proof before fix: pin the failing case (corpus entry,
   explorer vector, Coyote trace, PBT seed or regression proof) with its known-bad commit,
   watch it fail on the current head, fix, then re-run the corpus and both explorer
   targets (fixes in the reference PR introduced new defects twice). Generalize a
   finding into an oracle or dimension where possible. When CI yells, classify first:
   fix the product finding, not the harness.
6. **Pre-review gates** (subagents, see the table below). *Blast-radius rerun* on the
   final diff and *revert-each-fix* (revert each fix commit alone; its pinned case must
   yell) are MANDATORY (bookkeeping: they produce the *Blast radius* and *Proofs*
   sections). The *falsifier* and the *whole-tree reviewer* are advisory (no recorded
   proof; motivated by the reference PR, where every defect was found by a fresh
   reader). The semantic review of new normative sentences
   ([safety evidence](safety-evidence.md#semantic-review)) is MANDATORY (bookkeeping).

## Subagent roles and what each may see

| Role | Sees | Must not see | Tools |
| --- | --- | --- | --- |
| Lens (phase 2) | feature request, repository at base, prior-art files | design, plan, main agent's reasoning | read, grep |
| Falsifier (6) | user doc, public API, test project | implementation diff, author's tests | read, write and run tests |
| Blast radius (1, 6) | diff, call graph | nothing withheld | read, grep, analyzer |
| Revert-each-fix (6) | fix commits, pinned cases, CI commands | nothing withheld | git, run fuzz/tests |
| Whole-tree reviewer (6) | full tree at head, docs, ledgers | the PR description until done | read only |
| Implementer | everything | - | all |

Isolation is the variable that mattered: give each subagent a fresh context and only
its column. Use a different model family for the falsifier and the reviewer when
available. Prompts: `.claude/skills/implement-safely/prompts/`.

## Obligations by trigger

Commands are in [LiteDB.Fuzz](../../LiteDB.Fuzz/README.md) and the linked docs.

| Trigger in the diff | Obligation | Status |
| --- | --- | --- |
| New or changed `ILite*` member, new public type | Register `api:<Type>.<Member>` in `.github/safety/markers.json`, drive it from a target until the smoke campaign hits it (`check_reachability.py --base`, Reachability gate) | MANDATORY (bookkeeping) |
| Code that runs user callbacks, Shared/Direct lifetime, waits or handoffs | Run `transaction-interleavings,lifetime-chaos` (per-operation `Deadline`); give a new user-code point a callback or maintenance dimension ([explorer](../concurrency-explorer.md#extending-it)) | MANDATORY: proven generic at rows 1, 16, 23 and 27. Not fired at 21 other rows whose shape no generator produces ([generator gaps](https://github.com/litedb-org/LiteDB/issues/3091)) |
| New or changed wait, lock, lease, handoff | Instrument it in the [wait-for graph](../wait-for-graph.md#adding-a-blocking-site); keep the graph on; drivers register callback waits (`DriverWait`, `Join`) | MANDATORY: `self-wait` proven generic at rows 27 and 29 (29 with a caveat: present since the freeze); reproduction level at rows 12, 16 (`self-wait`) and 21 (`unbounded-cycle`); row 13 only with a tuned driver edge. A cancellation nobody requests counts as no bound (row 23, tuned). `bounded-cycle`, `lock-order`: report only (row 3 fired differently, row 4 not fired) |
| Shared core open/close, writer-mutex release | Keep `SharedOwnershipEvents` hooks at every core stage and release; run `Ownership` (`OwnershipMonitor`, `WatchOwnership`) | MANDATORY: proven generic at row 15 through the teardown sweep (caveat: the same yell already appears before the defect's commit); reproduction level at row 17 |
| A lifetime state machine in the [model correspondence](../concurrency-models.md#correspondence-table) | Update the Coyote model and its correspondence; run it | Advisory: row 13 proven at model level only (a model written from the known-bad code; the library itself was not run), and no CI check ties the model to the code |
| New or changed Dispose/Close/Release/finally path | `[TeardownPath(name, declared disposition, basis)]`, step markers, a driver and catalog steps ([teardown sweep](../teardown-sweep.md#adding-a-path-or-step)); `FaultDisposed` checks the declaration | MANDATORY (bookkeeping: the sweep fixture fails without a driver). Detection: proven generic at rows 9 and 15 (15 with a caveat), fired differently at row 29; row 17 only with tuned drivers; rows 6 and 7 not fired |
| New fault hook | Register it in `fault-points.json` with evidence | MANDATORY (bookkeeping, `check_fault_points.py`) |
| New fuzz target or harness scenario | Apply `Deadline`, `ConnectionClean`, `Quiescent`, `ScratchLive`, `Durable`, `FaultReached`/`FaultDisposed` or say why not ([validation](validation.md#fuzzing)) | MANDATORY (bookkeeping). Detection: `Quiescent` proven generic at row 9; rows 5 (transition + `Durable`) and 8 (`Quiescent` scratch) only with dimensions added after the freeze (tuned) |
| New subsystem paths | An obligation in `fuzz-obligations.json` (`select_fuzz_targets.py --validate`) | MANDATORY (bookkeeping) |
| Timed wait, sleep or spin in a loop in `LiteDB/` | `// polling: <reason>` (`lint_polling.py`) | MANDATORY: row 11 proven generic (a diff lint: it does not judge the unchanged poll line that the fix kept) |
| Deleted comment stating an invariant | `### Moved invariants` entry in the PR (`lint_invariant_comments.py`) | MANDATORY (bookkeeping: the PR section check lists every hit). Detection: row 11 only tuned (trigger words added after reading the deleted comment) |
| New or changed normative doc sentence | Inline anchor or `contracts.json` claim; non-author semantic review (`lint_doc_claims.py`, `check_contracts.py`) | MANDATORY (bookkeeping). The lint routes, it cannot judge truth (row 10 fired differently) |
| Intended behavior or exception-contract change | `intended-changes.json` entry with the promising doc sentence; racing operations declare `permitted` | MANDATORY (bookkeeping). The differential run itself is a pilot: not fired at rows 10 and 11 (known-bad and fix indistinguishable; it now reports claims it could not observe) |
| Parallel or alternative access paths | Permitted-history property test (`ParallelPropertyRunner`) | Advisory: not fired at rows 5, 12 and 14; row 12 only with a rule taken from later documentation (tuned) |
| Close, rebuild or fatal during other work | `chaos-maintenance` | Advisory: not fired at rows 1, 2, 7 and 15 (its declarations follow the base contract); it found dev defects (#3093, #3095, #3010) |
| Wait primitive, scheduler or hot path | Contention metrics (`measure-shared-contention.py`, `compare_contention.py`) with the tolerance in the PR | Pilot: row 11 fired differently (merge-base to known-bad, overtaking 4% to 30%; the fix does not restore it on Linux) |
| Cleanup or lock lines | Mutation on the diff (`run_mutation.py`) survivors listed | Pilot: row 11 configured scope exceeded a 60 min time box; a narrowed scope (65 min) left survivors but none on the defect line |
| Multi-process writers | `shared-contention` | Advisory: no proof |
| A new net, dimension or historical adapter | `net-proofs.json` entry with honest `level`, `independence` and `requires`; skeletons render "not attempted" (`net_proof.py validate`) | MANDATORY (bookkeeping) |
| A finding | Replayable case with its known-bad commit before the fix; a bug-fix PR adds a regression proof | MANDATORY (bookkeeping, `regression_proof.py`) |
| A quarantined test | Linked issue, owner, review and expiry dates; re-run weekly (`quarantine_rerun.py`) | MANDATORY (bookkeeping, `check_coverage_regression.py`) |

## Hard rules

- No production change larger than what a target already reaches: every marker the diff
  declares is hit before review.
- No normative doc sentence without an anchor or a `contracts.json` claim.
- No finding fixed without a replayable case pinned first; a fix starts from its
  recorded seed. A standalone xUnit test alone does not compose with random schedules.
- No quarantine without a linked issue and an expiry date; quarantined and known-finding
  tests (`Known_finding_*`) are re-run weekly.
- Deleting an invariant comment names where the invariant is enforced now.
- Never edit, skip, narrow or re-time a test or net to make CI green without a
  coverage-ledger disposition or manifest entry that explains the behavior change.
- Never present reproduction-level or tuned evidence as generic, a pilot as a gate,
  or not-applicable as passing.

## The PR description

A critical PR adds `## Critical change evidence` from the
[template](../../.github/pull_request_template.md), generated, not written:

```bash
python .github/scripts/check_reachability.py --base "$BASE" --runs <smoke dirs> --output reachability.json
python .github/scripts/render_critical_sections.py --base "$BASE" --reachability reachability.json \
  --proofs proofs.json --blast-radius blast.json [--benchmark diff.md | --no-hot-path "<reason>"]
```

*Intended changes* lists every manifest entry the PR adds; *Markers declared* every
declared marker with its hit count; *Proofs* each `known-bad → net → seed` and every
proof entry added; *Blast radius* each `widened contract → caller → disposition`;
*Benchmark diff* the measured diff or `No hot path touched: <reason>`. A subsection with
nothing to list says `None: <reason>`; a reason never replaces an entry the artifacts
require. `check_pr_section.py` enforces this for critical PRs only.

## Proven obligations

The nets behind the MANDATORY rows: every ledger entry that fired or could not run, and a
count of the entries that stayed quiet. *Level*: `generic` = a net not written for the defect
fired; `reproduction` = the fix's own test turned into an attributable yell; `model` = an
abstract model of the code fired; `tuned-after-fix` = the net, scenario or rule was changed
after reading the fix. *Independence*: `tuned` = changed after reading the fix. Only
**proven** counts. Rows number the reference defects (the `defects` table of
[net-proofs.json](safety-evidence.md#net-proofs)): 16 and 17 are upstream #3072 and #3077;
1-15 and 18-29 are defects of the transaction-handle PR (JKamsker/LiteDB#133), 18-29 being its
fixes after `e821ae74` and two merges. The [retrospective](../safety-net-retrospective.md)
holds the full matrix.

<!-- BEGIN proven-obligations: generated by .github/scripts/render_net_proofs.py; do not edit by hand -->
| Rows | Net | Level | Independence | Result | Assertion at known-bad | Proof |
| --- | --- | --- | --- | --- | --- | --- |
| 1 | lifetime-chaos | generic | designed | **proven** | DEADLINE_LIFETIME_CHAOS_FINDTRANSFORM_CALLBACK | `row1-lifetime-chaos` |
| 1 | lifetime-chaos | generic | designed | **proven** | DEADLINE_LIFETIME_CHAOS_FINDTRANSFORM_CALLBACK (also DEADLINE_LIFETIME_CHAOS_READ at 808:… | `row1-lifetime-chaos-coverage` |
| 3 | wait-for-graph | reproduction | designed | **fired-differently** | WAIT_FOR_CYCLE bounded-cycle (closing thread -> collection-lock waiter -> closing thread;… | `row3-wait-for-graph` |
| 5 | explorer: handle-disposed-bound-object … | tuned-after-fix | tuned | **proven** | EXPLORER_HANDLE_NOT_ACTIVE | `row5-explorer-transition` |
| 8 | teardown-sweep + crash-leftover prior (… | tuned-after-fix | tuned | **proven** | quiescent.scratch: sort scratch remains after close | `row8-crash-leftovers` |
| 9 | teardown-faults | generic | designed | **proven** | TEARDOWN_FAULTS_SHAREDENGINE_DISPOSE_QUIESCENT_HANDLES | `row9-teardown-faults` |
| 9 | teardown-sweep | generic | designed | **proven** | quiescent.handles / quiescent.readers / durable.REOPEN_FAILED after a fail-inside fault i… | `row9-teardown-sweep` |
| 10 | lint (c) doc claims | generic | designed | **fired-differently** | Normative sentence without an anchor (docs/transaction-handles.md:249, the close/checkpoi… | `row10-lint-c` |
| 11 | contention metrics (merge-base as basel… | generic | designed | **fired-differently** | overtaking rate rose 25.8 pp (> 5.0 pp): 4.0% -> 29.8% at 4 processes (base -> K') | `row11-contention-metrics` |
| 11 | lint (a) polling | generic | designed | **proven** | Timed wait/sleep/spin in a loop without '// polling: <reason>' (SharedMutexTurnstile.cs:7… | `row11-lint-a` |
| 11 | lint (b) deleted invariant comments | tuned-after-fix | tuned | **proven** | Deleted invariant comment needs a '### Moved invariants' entry (SharedMutexOwner.cs:437) | `row11-lint-b` |
| 11 | mutation pilot (configured scope) | generic | designed | **harness-error** | - | `row11-mutation-pilot` |
| 12 | parallel-property (handle kind, fail-fa… | tuned-after-fix | tuned | **proven** | [not-permitted] cb=Timeout:LockTimeout where only an early LockTimeout is permitted | `row12-parallel-property-tuned` |
| 12 | wait-for-graph | reproduction | designed | **proven** | WAIT_FOR_CYCLE:self-wait | `row12-wait-for-graph` |
| 13 | Coyote lifetime model | model | designed | **proven** | Liveness monitor: an operation neither completes nor is turned down (close fence vs callb… | `row13-coyote` |
| 13 | wait-for-graph | reproduction | tuned | **proven** | WAIT_FOR_CYCLE:unbounded-cycle (length 3; probe with the callback's wait as a driver edge) | `row13-wait-for-graph` |
| 15 | teardown-faults | generic | designed | **proven** | TEARDOWN_FAULTS_SHAREDENGINE_CHECKPOINTAFTERLASTREADER_OWNERSHIP_RELEASED_BEFORE_TEARDOWN | `row15-teardown-faults` |
| 15 | teardown-sweep | generic | designed | **proven** | ownership.RELEASED_BEFORE_TEARDOWN (SharedEngine.CheckpointAfterLastReader/self-callback-… | `row15-teardown-sweep` |
| 16 | explorer targets transaction-interleavi… | generic | designed | **proven** | DEADLINE_TRANSACTION_INTERLEAVINGS_READ (and DEADLINE_LIFETIME_CHAOS_INSERTINPUT_CALLBACK… | `row16-explorer` |
| 16 | wait-for-graph | reproduction | tuned | **proven** | WAIT_FOR_CYCLE:self-wait (report-only mode) | `row16-wait-for-graph` |
| 16 | wait-for-graph | reproduction | tuned | **proven** | WAIT_FOR_CYCLE:self-wait (failing rule) | `row16-wait-for-graph-rule` |
| 17 | ownership | reproduction | designed | **proven** | ownership.RELEASED_BEFORE_TEARDOWN | `row17-ownership` |
| 17 | teardown-sweep (self-callback drivers) | tuned-after-fix | tuned | **proven** | ownership.RELEASED_BEFORE_TEARDOWN (CheckpointAfterLastReader/self-callback-dispose) | `row17-teardown-sweep-tuned` |
| 17 | wait-for-graph | reproduction | designed | **fired-differently** | WAIT_FOR_CYCLE:self-wait on 2 of 12 failing cases (pin holder + nested getter); the relea… | `row17-wait-for-graph` |
| 21 | wait-for graph on the fix's own tests | reproduction | designed | **proven** | WAIT_FOR_CYCLE:unbounded-cycle (SharedMutexPin.WaitReleased <-> OperationLifetime.Exclusi… | `later-d9a5bfe6-reproduction` |
| 23 | lifetime-chaos | generic | designed | **proven** | DEADLINE_LIFETIME_CHAOS_INSERTINPUT_CALLBACK (and _FINDTRANSFORM_CALLBACK at 101:7) | `later-69b0663a-lifetime-chaos` |
| 23 | wait-for-graph (rule: an unrequested ca… | tuned-after-fix | tuned | **proven** | WAIT_FOR_CYCLE:unbounded-cycle (a cancellation-bounded wait whose token nobody requests) | `later-69b0663a-wait-for-graph-cancellation-rule` |
| 27 | lifetime-chaos | generic | designed | **proven** | DEADLINE_LIFETIME_CHAOS_UPLOAD_CALLBACK | `later-c6e6c848-lifetime-chaos` |
| 27 | transaction-interleavings | generic | designed | **proven** | DEADLINE_TRANSACTION_INTERLEAVINGS_READ (Shared callback=peer) | `later-c6e6c848-transaction-interleavings` |
| 27 | wait-for-graph | generic | designed | **proven** | WAIT_FOR_CYCLE:self-wait (failing rule) | `later-c6e6c848-wait-for-graph` |
| 29 | teardown-sweep | generic | designed | **fired-differently** | deadline.case (55 s) on SharedMutexPin.Hold/self-callback-getter and -dispose, with a fai… | `later-56c8680a-teardown-sweep` |
| 29 | wait-for-graph | generic | designed | **proven** | WAIT_FOR_CYCLE:self-wait (pin holder waits at SharedMutexOwner.Enter for the mutex it hol… | `later-56c8680a-wait-for-graph` |

Not fired (120 entries, listed in the full matrix of docs/safety-net-retrospective.md): explorer targets (Deadline) 50, teardown sweep / teardown-faults 30, wait-for graph 23, chaos-maintenance 6, parallel property (permitted histories) 3, differential run 2, composed re-run after a fix 2, fuzz target sort 1, contention metrics 1, mutation pilot 1, Ownership oracle 1.

26 of 152 entries proven. Rendered from `.github/safety/net-proofs.json`; refresh with `python .github/scripts/render_net_proofs.py --write docs/rules/implement-safely.md`.
<!-- END proven-obligations -->
