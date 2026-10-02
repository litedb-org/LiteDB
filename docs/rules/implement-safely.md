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
check enforces. A proof that exists only for a net tuned after reading the fix does not
make a net mandatory by itself. Everything else is **advisory** or a **pilot**, with the
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
| Code that runs user callbacks, Shared/Direct lifetime, waits or handoffs | Run `transaction-interleavings,lifetime-chaos` (per-operation `Deadline`); give a new user-code point a callback or maintenance dimension ([explorer](../concurrency-explorer.md#extending-it)) | MANDATORY: row 16 proven generic. Gap: no storage-stream callback during teardown (row 17 not fired) |
| New or changed wait, lock, lease, handoff | Instrument it in the [wait-for graph](../wait-for-graph.md#adding-a-blocking-site); keep the graph on; drivers register callback waits (`DriverWait`, `Join`) | MANDATORY: `self-wait` (rows 12, 16) and `unbounded-cycle` (row 13, tuned driver edge) fail by default, reproduction level. `bounded-cycle`, `lock-order`: report only (row 3 fired differently, row 4 not fired) |
| Shared core open/close, writer-mutex release | Keep `SharedOwnershipEvents` hooks at every core stage and release; run `Ownership` (`OwnershipMonitor`, `WatchOwnership`) | MANDATORY: row 17 proven, reproduction level |
| A lifetime state machine in the [model correspondence](../concurrency-models.md#correspondence-table) | Update the Coyote model and its correspondence; run it | MANDATORY: row 13 proven generic (model written from the changed code) |
| New or changed Dispose/Close/Release/finally path | `[TeardownPath(name, declared disposition, basis)]`, step markers, a driver and catalog steps ([teardown sweep](../teardown-sweep.md#adding-a-path-or-step)); `FaultDisposed` checks the declaration | MANDATORY (bookkeeping: the sweep fixture fails without a driver). Detection advisory: row 17 not fired generically |
| New fault hook | Register it in `fault-points.json` with evidence | MANDATORY (bookkeeping, `check_fault_points.py`) |
| New fuzz target or harness scenario | Apply `Deadline`, `ConnectionClean`, `Quiescent`, `ScratchLive`, `Durable`, `FaultReached`/`FaultDisposed` or say why not ([validation](validation.md#fuzzing)) | MANDATORY (bookkeeping). Detection advisory: rows 5, 8, 9 not yet proven |
| New subsystem paths | An obligation in `fuzz-obligations.json` (`select_fuzz_targets.py --validate`) | MANDATORY (bookkeeping) |
| Timed wait, sleep or spin in a loop in `LiteDB/` | `// polling: <reason>` (`lint_polling.py`) | MANDATORY: row 11 proven generic |
| Deleted comment stating an invariant | `### Moved invariants` entry in the PR (`lint_invariant_comments.py`) | MANDATORY: row 11 proven, tuned (trigger words added after reading the comment) |
| New or changed normative doc sentence | Inline anchor or `contracts.json` claim; non-author semantic review (`lint_doc_claims.py`, `check_contracts.py`) | MANDATORY (bookkeeping). The lint routes, it cannot judge truth (row 10 fired differently) |
| Intended behavior or exception-contract change | `intended-changes.json` entry with the promising doc sentence; racing operations declare `permitted` | MANDATORY (bookkeeping). The differential run itself is a pilot: no proof yet (row 10 not attempted), false positives on undeclared races |
| Parallel or alternative access paths | Permitted-history property test (`ParallelPropertyRunner`) | Advisory: rows 12 and 14 not fired by the generic net |
| Close, rebuild or fatal during other work | `chaos-maintenance` | Advisory: no ledger proof yet (it found dev defects) |
| Wait primitive, scheduler or hot path | Contention metrics (`measure-shared-contention.py`, `compare_contention.py`) with the tolerance in the PR | Pilot: row 11 not fired on Linux (environment-dependent) |
| Cleanup or lock lines | Mutation on the diff (`run_mutation.py`) survivors listed | Pilot: no proof, cost unmeasured |
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

The nets behind the MANDATORY rows, one row per proof record. *Level*: `generic` = a net
not written for the defect fired; `reproduction` = the fix's own test turned into an
attributable yell. *Independence*: `tuned` = the net was changed after reading the fix.
Only **proven** counts. Rows number the reference defects: 16 and 17 are upstream #3072
and #3077, the others defects of the transaction-handle PR (JKamsker/LiteDB#133); each
[net-proofs.json](safety-evidence.md#net-proofs) entry names its `defect`.

<!-- BEGIN proven-obligations: generated by .github/scripts/render_net_proofs.py; do not edit by hand -->
| Rows | Net | Level | Independence | Result | Assertion at known-bad | Proof |
| --- | --- | --- | --- | --- | --- | --- |
| 3 | wait-for-graph | reproduction | designed | **fired-differently** | Reporting/rule view: bounded-cycle, not a failing rule. Raw_close_interrupts_collection_w… | `row3-wait-for-graph` |
| 4 | wait-for-graph | reproduction | designed | **not-fired** | none. The fix's test (begin with TimeSpan.Zero) fails with TimeoutException instead of In… | `row4-wait-for-graph` |
| 10 | lint-c | generic | designed | **fired-differently** | ERROR docs/transaction-handles.md:249: Normative sentence without a [test: ...] or [marke… | `row10-lint-c` |
| 11 | contended-acquire | generic | designed | **not-fired** | - | `row11-contention-benchmark` |
| 11 | lint-a | generic | designed | **proven** | ERROR SharedEngine.Waiters.cs:105 `Monitor.Wait(_waitersLock, 10);` / SharedMutexOwner.cs… | `row11-lint-a` |
| 11 | lint-b | generic | tuned | **proven** | SharedMutexOwner.cs:437 Deleted invariant comment needs a '### Moved invariants' entry: "… | `row11-lint-b` |
| 12 | parallel-property | generic | designed | **not-fired** | - | `row12-parallel-property` |
| 12 | parallel-property (tuned driver/rule) | generic | tuned | **proven** | [not-permitted] seed 1020: handle.InsertBulk+cb:Upsert/c0/5#1 observed 'cb=Timeout:LockTi… | `row12-parallel-property-tuned` |
| 12 | wait-for-graph | reproduction | designed | **proven** | WAIT_FOR_CYCLE:self-wait in 4/4 failing cases of Same_collection_callback_refuses_self_wa… | `row12-wait-for-graph` |
| 13 | coyote-lifetime-model | generic | designed | **proven** | Liveness violated: every operation completes or is rejected. No thread can make progress … | `row13-coyote` |
| 13 | wait-for-graph | reproduction | tuned | **proven** | WAIT_FOR_CYCLE:unbounded-cycle (probe, unbounded variant): "Wait-for cycle, unbounded-cyc… | `row13-wait-for-graph` |
| 14 | parallel-property | generic | designed | **not-fired** | - | `row14-parallel-property` |
| 16 | concurrency-explorer | generic | designed | **proven** | transaction-interleavings: DEADLINE_TRANSACTION_INTERLEAVINGS_READ at step 4 (vector scen… | `row16-explorer` |
| 16 | wait-for-graph | reproduction | tuned | **proven** | Reporting mode (no throw, enforcement is a later step): 28/28 hanging test cases latched … | `row16-wait-for-graph` |
| 16 | wait-for-graph | reproduction | tuned | **proven** | 28/28 hanging SharedPeer* cases latch self-wait with failing=true; the xUnit hook adds 'T… | `row16-wait-for-graph-rule` |
| 17 | concurrency-explorer | generic | designed | **not-fired** | No row-17 yell. Final campaign (net up to 0b272e93d): lifetime-chaos passed 200/200 (930-… | `row17-explorer` |
| 17 | ownership | reproduction | designed | **proven** | RELEASED_BEFORE_TEARDOWN: Thread 'LiteDB shared mutex owner' released the writer mutex wh… | `row17-ownership` |
| 17 | teardown-sweep | generic | designed | **not-fired** | - | `row17-teardown-sweep` |
| 17 | teardown-sweep (tuned driver/rule) | generic | tuned | **proven** | Sweep: 3 failed of 32. SharedEngine.CheckpointAfterLastReader/self-callback-dispose basel… | `row17-teardown-sweep-tuned` |
| 17 | wait-for-graph | reproduction | designed | **fired-differently** | 2 of the 12 failing cases latched self-wait: close=PinHolder, nested=Getter (encrypted fa… | `row17-wait-for-graph` |

11 of 20 entries proven. Rendered from the milestone proof records of 2026-10-02 (not yet merged into `.github/safety/net-proofs.json`, which holds skeletons); refresh with `python .github/scripts/render_net_proofs.py --write docs/rules/implement-safely.md`.
<!-- END proven-obligations -->
