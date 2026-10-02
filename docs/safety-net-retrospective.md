# Safety-net retrospective: the nets against the JKamsker/LiteDB#133 history

This is the final report of the safety-net plan (#3034): which general nets would have caught the
defects of the transaction-handle PR [JKamsker/LiteDB#133](https://github.com/JKamsker/LiteDB/pull/133)
and of the two upstream fixes #3072 and #3077, where the plan was wrong, what the nets cost per PR, and
what to invest in next. Every number comes from a ledger entry of
[`.github/safety/net-proofs.json`](../.github/safety/net-proofs.json); the tables between the
`generated` markers are rendered from that ledger by `.github/scripts/render_net_proofs.py` and checked
in CI, so they cannot drift from it.

## Summary

- **29 defect rows** were checked: the plan's 15 rows from JKamsker/LiteDB#133, the two upstream rows
  (#3072, #3077), the PR's 10 production fixes after `e821ae74` and its two merges that bring in the
  #3072 fix and its own #3077 twin. 152 ledger entries record one net at one row each.
- **A net not written for the defect, unchanged after reading the fix, proved 8 rows** (1, 9, 11, 15,
  16, 23, 27, 29); two of them (15, 29) with the caveat that the same yell was already present before the
  defect's commit. 3 rows fire only when the fix's own test drives the net (12, 17, 21), 1 only on an
  abstract model (13), 2 only with a dimension added after the freeze (5, 8), 2 fired differently
  (3, 10), and **13 rows were caught by no net**.
- The misses are **generation gaps, not oracle gaps**: in every not-fired row the verifier found a
  shape no generator produces (sustained readers, a raw engine close, a begin under a held
  reader, callbacks that begin work, in-library pause points, ...). They are tracked in #3091.
- The three nets that fired most are the **explorer targets with their per-operation Deadline**
  (`transaction-interleavings`, `lifetime-chaos`), the **wait-for graph** and the **teardown sweep**
  (with `teardown-faults`). Only the explorer targets proved more than two rows generically.
- The verification found **12 defects on current `dev`**: nine issues (#3093 to #3101), a comment on
  #3010, one already known (#2163, #3041) and one documentation gap. It also found **nine defects in the
  nets themselves**, fixed or recorded before this report.

## How the rows were checked

| Rows | Trees | Nets | Report |
| --- | --- | --- | --- |
| 16, 17 | upstream known-bad and fix commits with the milestone's nets ported on top | wait-for graph, Ownership, explorer targets, teardown sweep | M1, M2, M2b, M3 |
| 1-15, 18-29 | `proof/pr133-replay`: the integrated nets frozen at `047a9a41d` and cherry-picked onto `39f6c6b0` with six historical adapters (tag `proof/pr133-nets-frozen`), then all 103 first-parent PR commits replayed on top; every known-bad K and fix F has a replay K'/F' | explorer targets, chaos-maintenance, teardown sweep and teardown-faults, parallel property, differential run, contention metrics | REPLAY, V-A, V-A2, V-B, V-C, V-D, V-E1, V-E2 |
| 3, 4, 12, 13 | the plain PR commits with the wait-for graph ported per commit | wait-for graph | V-waitgraph |
| 12, 14 (and 5) | the plain PR commits with the parallel property test and its handle access kind | parallel property | V-pbt |
| 10, 11 | revisions read by the diff lints; production builds for the contention runs | lints (a)-(c), contention metrics, mutation pilot | M4, V-D |
| 13 | an abstract Coyote model of the code at each commit | Coyote lifetime model | M5 |

**Generic before anything else.** Each verifier ran the frozen nets with the same seeds and budget on
K' and F' and compared failure sets *before* reading the fix's diff, test or message. Only afterwards
were the fix's own tests run with the nets active (level `reproduction`) or nets extended (level
`tuned-after-fix`, recorded as a separate entry). A net that stayed quiet was recorded as `not-fired`
with the reason; no net was weakened or re-timed to pass.

**Coverage correction.** On PR trees before the #3072 merge most explorer seeds stopped within a few
steps on the base revision's #3071 hang, identically on both sides. A second pass (V-A2, and every
later-fix pair) registered that *other* defect as a known finding and declared the PR's documented
close contract for chaos-maintenance
([`pr133-coverage`](../tools/net-proofs/adapters/pr133-coverage/README.md)). It changes no oracle,
deadline or generator draw. It masks the #3071 class itself, so row 27 ran its known-bad side without it.

**Evidence classes.** Class 1 (forced schedules, models, teardown step faults) replays to the same
assertion; class 2 (native threads, Shared holder threads, real processes) records a reproduction rate
over 10 or more reruns with the machine load; class 3 (contention) compares repeated paired runs with
stated tolerances. The host was shared (1-minute load 6-143 during the runs); no timing-only yell was
counted when its replay at lower load did not reproduce it.

**Re-running.** Recorded entries are not re-run by CI (`net_proof.py run` reports them as
`not-attempted`). The adapters they used are in [`tools/net-proofs/adapters/`](../tools/net-proofs/README.md);
the replay branch `proof/pr133-replay`, the tag `proof/pr133-nets-frozen` and the per-row `proof/*`
branches named in each entry's `net.recorded` exist in the verification clone and still have to be
published before anyone else can re-run them.

## Verification matrix

One line per defect row. *Proven by* names the ledger entries; the [appendix](#appendix-every-ledger-entry)
lists every entry with its class, time to yell and reproduction rate.

<!-- BEGIN verification-matrix: generated by .github/scripts/render_net_proofs.py; do not edit by hand -->
| Row | Defect | Known-bad → fix | Strongest evidence | Proven by | Fired differently | Stayed quiet (families) |
| --- | --- | --- | --- | --- | --- | --- |
| 1 | Shared close waits forever on an uncancellable native admission | `5dcba2f0` → `70429cb6` | **generic** | `row1-lifetime-chaos` (generic), `row1-lifetime-chaos-coverage` (generic) | - | chaos-maintenance, explorer targets (Deadline), wait-for graph |
| 2 | Pending rebuild/close starves fresh readers while dependencies finish | `e28612aa` → `2124767a` | **none** | - | - | chaos-maintenance, explorer targets (Deadline) |
| 3 | Raw engine close deadlocks with collection-lock waiters | `e28612aa` → `2124767a` | **fired differently only** | - | `row3-wait-for-graph` | explorer targets (Deadline), wait-for graph |
| 4 | Handle begin under a caller-held Shared reader/pin makes no progress | `70429cb6` → `e28612aa` | **none** | - | - | explorer targets (Deadline), wait-for graph |
| 5 | Disposed reader/enumerator race aborts the transaction and loses prio… | `26406eca` → `9bf6137c` | **tuned** | `row5-explorer-transition` (tuned) | - | explorer targets (Deadline), parallel property (permitted histories) |
| 6 | Handle/child disposal inside `using` masks the real exception | `26406eca` → `9bf6137c` | **none** | - | - | explorer targets (Deadline), teardown sweep / teardown-faults |
| 7 | Fatal publication vs completed teardown suppressed wrongly (+ deferre… | `4b8a3e12` → `ac863c0a` | **none** | - | - | chaos-maintenance, explorer targets (Deadline), teardown sweep / teardown-faults |
| 8 | Sort scratch file deleted under a live reader / left stale | `39f6c6b0` → `5dcba2f0` | **tuned** | `row8-crash-leftovers` (tuned) | - | explorer targets (Deadline), fuzz target sort, teardown sweep / teardown-faults |
| 9 | Pin-close error skips the rest of Shared cleanup (unchanged caller of… | `0d5e5eff` → `a3313561` | **generic** | `row9-teardown-faults` (generic), `row9-teardown-sweep` (generic) | - | - |
| 10 | Docs say close/checkpoint errors reach the caller; the Shared parent … | `a3313561` → `2bce2091` | **fired differently only** | - | `row10-lint-c` | differential run |
| 11 | Every Shared mutex wait became a timed poll (introduced in 70429cb6, … | `eb01f346` → `47edadef` | **generic** | `row11-lint-a` (generic), `row11-lint-b` (tuned) | `row11-contention-metrics` | contention metrics, differential run, mutation pilot |
| 12 | Same-thread self-wait detection lost when the lock owner key changed … | `e2228105` → `d189f7a8` | **reproduction** | `row12-parallel-property-tuned` (tuned), `row12-wait-for-graph` (repro) | - | parallel property (permitted histories), wait-for graph |
| 13 | Close fence deadlocks an active op that depends on fresh work on anot… | `cb36c346` → `0d5e5eff` | **model** | `row13-coyote` (model), `row13-wait-for-graph` (repro) | - | explorer targets (Deadline), wait-for graph |
| 14 | Fix for row 12 restored the execution scope after releasing admission… | `d189f7a8` → `9d63ccc2` | **none** | - | - | composed re-run after a fix, explorer targets (Deadline), parallel property (permitted histories), wait-for graph |
| 15 | Intermediate cleanup fix released native ownership while the core was… | `6831e41b` → `be1e630c` | **generic (caveat)** | `row15-teardown-faults` (generic), `row15-teardown-sweep` (generic) | - | Ownership oracle, chaos-maintenance, composed re-run after a fix, explorer targets (Deadline), wait-for graph |
| 16 | Shared peer call waits for its own outer ownership (#3072) | `5dd942a7` → `265c2497` | **generic** | `row16-explorer` (generic), `row16-wait-for-graph` (repro), `row16-wait-for-graph-rule` (repro) | - | wait-for graph |
| 17 | Same-connection teardown callback loses Shared writer ownership (#307… | `023c2b4b` → `7b71bc4d` | **reproduction** | `row17-ownership` (repro), `row17-teardown-sweep-tuned` (tuned) | `row17-wait-for-graph` | explorer targets (Deadline), teardown sweep / teardown-faults |
| 18 | A Shared core drain gives up reader ownership while a late reader's c… | `8a49d584` → `9199637e` | **none** | - | - | explorer targets (Deadline), teardown sweep / teardown-faults, wait-for graph |
| 19 | A fresh Shared call arriving while the last reader's storage core ret… | `22fe0b4c` → `3166d49c` | **none** | - | - | explorer targets (Deadline), teardown sweep / teardown-faults, wait-for graph |
| 20 | Facade Dispose from a late reader's callback, whose storage the close… | `3166d49c` → `c20deb01` | **none** | - | - | explorer targets (Deadline), teardown sweep / teardown-faults, wait-for graph |
| 21 | A core callback that disposes the last leased reader joins that reade… | `f95f0b0c` → `d9a5bfe6` | **reproduction** | `later-d9a5bfe6-reproduction` (repro) | - | explorer targets (Deadline), teardown sweep / teardown-faults, wait-for graph |
| 22 | After a callback's self-dispose of its leased snapshot reader is turn… | `d9a5bfe6` → `f8de2bb9` | **none** | - | - | explorer targets (Deadline), teardown sweep / teardown-faults, wait-for graph |
| 23 | A Shared handle callback that calls its own connection waits forever … | `12a05742` → `69b0663a` | **generic** | `later-69b0663a-lifetime-chaos` (generic), `later-69b0663a-wait-for-graph-cancellation-rule` (tuned) | - | explorer targets (Deadline), teardown sweep / teardown-faults, wait-for graph |
| 24 | A handle callback that begins another handle on the same Shared datab… | `1683f912` → `2a1549a6` | **none** | - | - | explorer targets (Deadline), teardown sweep / teardown-faults, wait-for graph |
| 25 | A handle callback write, while a caller-held leased reader anchors a … | `569ba13c` → `c172623f` | **none** | - | - | explorer targets (Deadline), teardown sweep / teardown-faults, wait-for graph |
| 26 | BeginTransaction inside an ordinary Shared call's callback that retai… | `c172623f` → `ab8aa1ed` | **none** | - | - | explorer targets (Deadline), teardown sweep / teardown-faults, wait-for graph |
| 27 | Shared peer call from a callback waits for its own outer ownership (t… | `98a10086` → `c6e6c848` | **generic** | `later-c6e6c848-lifetime-chaos` (generic), `later-c6e6c848-transaction-interleavings` (generic), `later-c6e6c848-wait-for-graph` (generic) | - | teardown sweep / teardown-faults |
| 28 | BeginTransaction on a peer facade from a caller LogStream callback, w… | `e4f14c5f` → `0d7ea1ee` | **none** | - | - | explorer targets (Deadline), teardown sweep / teardown-faults, wait-for graph |
| 29 | Same-connection reentry from a pin-close teardown callback waits for … | `3abead6d` → `56c8680a` | **generic (caveat)** | `later-56c8680a-wait-for-graph` (generic) | `later-56c8680a-teardown-sweep` | explorer targets (Deadline), teardown sweep / teardown-faults |

*Strongest evidence*: generic > model > reproduction > tuned among proven entries; *(caveat)*: a proving entry records a caveat (see its `results.caveat`). Rendered from `.github/safety/net-proofs.json`; refresh with `python .github/scripts/render_net_proofs.py --write <file>`.
<!-- END verification-matrix -->

Time to yell, generic firings (first attributable yell, wall clock): row 1 `lifetime-chaos` 62 s inside
the failing run, 495-633 s into a campaign; row 9 teardown sweep 2 s for the theory (223 s for the
whole sweep), `teardown-faults` 34 s at the failing seed; row 11 lint (a) 0.1 s; row 15 sweep 2.2 s,
`teardown-faults` 9 s; row 16 `transaction-interleavings` 21 s, `lifetime-chaos` 77 s; row 23 192 s;
row 27 graph under 5 s, `transaction-interleavings` 138 s, `lifetime-chaos` 398 s; row 29 graph 2 s.

## Which nets fired

<!-- BEGIN net-firings: generated by .github/scripts/render_net_proofs.py; do not edit by hand -->
| Net family | Generic proven (rows) | Fired differently | Reproduction | Model | Tuned | Not fired (rows) |
| --- | --- | --- | --- | --- | --- | --- |
| explorer targets (Deadline) | 4: 1, 16, 23, 27 | 0 | 0 | 0 | 1: 5 | 21: 2, 3, 4, 5, 6, 7, 8, 13, 14, 15, 17, 18, 19, 20, 21, 22, 24, 25, 26, 28, 29 |
| wait-for graph | 2: 27, 29 | 2: 3, 17 | 4: 12, 13, 16, 21 | 0 | 1: 23 | 17: 1, 4, 12, 13, 14, 15, 16, 18, 19, 20, 21, 22, 23, 24, 25, 26, 28 |
| teardown sweep / teardown-faults | 2: 9, 15 | 1: 29 | 0 | 0 | 2: 8, 17 | 15: 6, 7, 8, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28 |
| lint (a): polling | 1: 11 | 0 | 0 | 0 | 0 | 0 |
| contention metrics | 0 | 1: 11 | 0 | 0 | 0 | 0 |
| lint (c): doc claims | 0 | 1: 10 | 0 | 0 | 0 | 0 |
| Ownership oracle | 0 | 0 | 1: 17 | 0 | 0 | 1: 15 |
| Coyote model | 0 | 0 | 0 | 1: 13 | 0 | 0 |
| chaos-maintenance | 0 | 0 | 0 | 0 | 0 | 4: 1, 2, 7, 15 |
| composed re-run after a fix | 0 | 0 | 0 | 0 | 0 | 2: 14, 15 |
| differential run | 0 | 0 | 0 | 0 | 0 | 2: 10, 11 |
| fuzz target sort | 0 | 0 | 0 | 0 | 0 | 1: 8 |
| lint (b): deleted invariant comments | 0 | 0 | 0 | 0 | 1: 11 | 0 |
| mutation pilot | 0 | 0 | 0 | 0 | 0 | 1: 11 |
| parallel property (permitted histories) | 0 | 0 | 0 | 0 | 1: 12 | 3: 5, 12, 14 |

Rows are defect rows of the ledger, counted once per family and column. *Not fired* lists rows where an entry of the family stayed quiet and none of its entries proved the row generically or fired differently (a row can also appear under reproduction or tuned). Rendered from `.github/safety/net-proofs.json`; refresh with `python .github/scripts/render_net_proofs.py --write <file>`.
<!-- END net-firings -->

**The three nets that fired most**, counting generic firings apart from reproduction and tuned ones:

1. **Explorer targets with a per-operation Deadline** (`transaction-interleavings`, `lifetime-chaos`):
   generic at rows 1, 16, 23 and 27. Rows 16 and 27 are the same defect (#3071/#3072) on `dev` and inside
   the PR, so this is three distinct defects. All four are hangs where a call waits for something only
   its own thread or an already closing session could release; the Deadline turns them into an
   attributable failure with stacks. `lifetime-chaos` fired at all four, `transaction-interleavings` at
   16 and 27. One more row (5) fires with a post-freeze scenario (tuned).
2. **Wait-for graph** (default fail rules `self-wait`, `unbounded-cycle`): generic only at rows 27 and 29,
   both `self-wait`, and row 29's yell is present at every replay tree since the freeze. Its strength is
   attribution once a scenario reaches the shape: reproduction level at rows 12, 16 (`self-wait`), 21
   (`unbounded-cycle`) and 13 (`unbounded-cycle`, with a probe written after reading the fix's test),
   fired differently at rows 3 and 17, tuned at row 23. On the existing test suite
   at the known-bad commits it fired at none of rows 3, 4, 12, 13, 16 (the plan expected all of them).
3. **Teardown sweep and `teardown-faults`** (step faults judged by `Quiescent`, `ConnectionClean`,
   `Ownership`, `Durable`): generic at rows 9 and 15 (15 with the caveat), fired differently at 29, tuned
   at 8 and 17. It also caught a regression the fix `9199637e` introduced (see below).

Single firings: lint (a) at row 11 (generic), contention metrics at row 11 (fired differently), lint (c)
at row 10 (fired differently), the Coyote model at row 13 (model level), the Ownership oracle at row 17
(reproduction). Not fired anywhere: chaos-maintenance (4 rows), the parallel property (3 rows, one tuned
firing), the differential run (2 rows), the composed re-run (2 rows), the mutation pilot (1 row).

**What to invest in next.** More seeds of the same generators buy nothing: every not-fired verdict
rests on a shape that is not generated, and coverage-corrected second passes with 1.5-2.2x the programs
changed no verdict. The return is in new generator dimensions (#3091, next section but one), in
test-only pause points inside the library for the explorer, and in standard, capability-gated
known-finding exclusion so a known base defect does not end every campaign after a few steps.

## Rows the plan got wrong

- **Row 12 was not a hang under the documentation of its time.** At `e2228105` a callback's write
  against the executing handle's own lock waits the whole TIMEOUT and then fails with LOCK_TIMEOUT, which
  `docs/transaction-handles.md` permitted there. The permitted-history property is quiet; it fires only
  with the fail-fast rule from documentation written after `e821ae74` (`row12-parallel-property-tuned`).
  The wait-for graph reports the length-1 cycle only on the fix's own test (`row12-wait-for-graph`).
- **Row 15's yell is not fix-induced.** The Ownership oracle in the teardown sweep reports
  `RELEASED_BEFORE_TEARDOWN` at K' and not at F', but the identical case fails at K's parent and at the
  freeze point: the class predates the "intermediate cleanup fix", so the net would not have flagged
  that commit as a regression. The plan's `held ⇔ core open` equivalence was itself wrong (it fails the
  valid ownership-without-core intervals of open and close); the shipped oracle is a generation-tracked
  implication.
- **Turning point 5 (a composed re-run after every fix) gives no signal on trees that are red before
  the fix.** At rows 14 and 15 the corpus plus every new target's smoke run is red at K' and equally red
  at F' on pre-existing failures (`row14-composed-rerun`, `row15-composed-rerun`). The claim needs CI to be
  green before each fix; and row 14 still needs a generator for its race window.
- **Row 11 is not Windows-only and is not fixed on Linux.** The timed poll is active on Linux
  (strace: timed futex waits replace the base's untimed ones); measured against the merge-base the
  overtaking rate at 4 writers rises from about 4% to 30% at K', stays at 31% at F' and is 46% at
  the PR head (against 9% for the base in that pair) (`row11-contention-metrics`). M4's first comparison used F' as the baseline for K', a pair
  that cannot differ on Linux (`row11-contention-benchmark`). The plan's "starvation" count measures
  overtaking. Lint (a) fires at the introducing commit `70429cb6`, but a diff lint does not re-judge the
  unchanged Unix poll line the fix kept.
- **Row 10 needs a reviewer, and the differential run could not see it.** Lint (c) flags the
  sentence at K' and equally at F' (68 unanchored claims): an anchor proves a reference exists, not that
  the sentence is true. The differential run could not tell K' from F' because no target drives a
  Dispose into a close or checkpoint fault, "exercised and unchanged" read the same as "not exercised"
  (fixed since in `8975a3d18`), and the fix is documentation-only for this behavior. A desk application
  of the contracts review forces a look at the Shared parent's close path but does not force the right
  answer (V-D; not a ledger entry because no net ran).
- **Rows 2, 3, 4 and 13 were predicted for `lifetime-chaos` and the graph; none fired.** Their shapes
  are absent from the generators: a *sustained* stream of fresh readers starving maintenance (row 2), a
  raw `ILiteEngine` close with collection-lock waiters (row 3; and its stall lasts one lock TIMEOUT, under
  the declared 60 s close deadline), a begin on a thread that already holds a reader or pin (row 4; the
  graph also has no executor for an idle hold), a dependency on fresh work on the same file during a
  close (row 13). Row 13 fires on the Coyote model and, with a driver edge written after reading the fix's
  test, on the graph.
- **Rows 5, 6, 7 and 8 expected the outcome and leak oracles to fire on existing shapes.** Row 5 is
  vacuous for `Durable` without a transition check (no commit is acknowledged before the abort), and no
  generator uses a disposed bound reader while its handle is Active; the post-freeze transition scenario
  fires (tuned). Row 6: no net disposes a handle or child while a primary exception is in flight. Row 7:
  `EngineState.Stop` declares two acceptable dispositions and no net publishes a fatal after a completed
  teardown. Row 8: no frozen target starts from a crash leftover; the added crash-leftover prior fires
  (tuned, from the fix's subject line).
- **Row 14 needs a pause point inside the library.** The race lies between the admission release and
  the scope restore inside one call; the explorer pauses only at user callbacks and whole-call
  boundaries, and five property-test attempts did not hit it natively.
- **Rows 16 and 17 did not fire inside the existing suite.** The plan expected the wait-for graph to
  fire "with no new tests"; at 5dd942a7 the 574 existing Shared tests latch nothing. Row 16 fires
  generically through the explorer's peer-callback dimension; row 17 needs storage-stream callbacks
  during close, which only the drivers added after reading #3077's test provide.
- **The later fixes are not "the same class as rows 12-13, caught by the same nets".** Of the 10
  production fixes after `e821ae74`, one is caught generically (23, by `lifetime-chaos`; the graph types
  its owner wait as cancellation-bounded) and one at reproduction level (21). The merges are caught
  (27, 29), with the caveats above.
- **Smaller corrections.** The wait-for graph needed typed edges, latched reports and rules enabled
  one at a time instead of a universal deadlock exception (a cancellation-bounded wait whose token nobody
  requests was still counted as bounded until `fc83c4380`). Mutation on the diff cannot express row 11
  (no mutator restores a blocking wait) and the configured scope did not finish in 60 minutes. Native
  stress is evidence class 2, not deterministic: row 1 reproduces 8/10 at K'.

## Defects after `e821ae74` and which net catches each

| Row | Fix | Caught by | Why not (generic) |
| --- | --- | --- | --- |
| 18 | `9199637e` | none (the sweep caught the fix's own regression, below) | no late-reader callback disposes or re-enters its connection while a foreign close drains a mutex-backed snapshot or pinned core; `_useLock` entry is no graph site |
| 19 | `3166d49c` | none | no schedule point inside last-reader core retirement; a fresh call does not land in that window |
| 20 | `c20deb01` | none (graph with the non-default `bounded-cycle` rule on the fix's tests) | the generators close only leased readers through connection-string facades; the self-drain is typed bounded |
| 21 | `d9a5bfe6` | wait-for graph `unbounded-cycle`, reproduction level (`later-d9a5bfe6-reproduction`) | no callback disposes another (last leased) reader while its core drains |
| 22 | `f8de2bb9` | none | no callback disposes the reader it is reading; leak oracles are not attached to plain xUnit tests |
| 23 | `69b0663a` | `lifetime-chaos` Deadline, generic (`later-69b0663a-lifetime-chaos`); the graph after `fc83c4380` (tuned) | - |
| 24 | `2a1549a6` | none | no callback begins a unit; Shared await children use the next file |
| 25 | `c172623f` | none | no leased reader held across a handle callback write |
| 26 | `ab8aa1ed` | none | no callback begins a handle; no raw `SharedEngine` ReadTransform driver |
| 27 | merge `c6e6c848` (#3072) | `transaction-interleavings`, `lifetime-chaos`, graph `self-wait`, generic, known-bad side without the coverage patch | - |
| 28 | `0d7ea1ee` | none | no peer handle begin from a stream callback during core close |
| 29 | merge `56c8680a` (#3077 twin) | graph `self-wait` (generic) and the sweep's case Deadline (fired differently); Ownership quiet | yell present since the freeze |

## Other findings on the JKamsker/LiteDB#133 history

- **A regression introduced by a fix.** `9199637e` moves the pin release after the core close; a
  same-connection Dispose from that close's write callback then waits for its own pin. The teardown
  sweep (case Deadline 55 s) and the graph (`self-wait` at `SharedMutexPin.WaitReleased`) report it 3/3
  at F' and 0/3 at K', on every later tree until the merge `56c8680a` (`later-9199637e-teardown-sweep`,
  `results.finding`).
- **Row 11 persists on Linux at the PR head** (overtaking 9% to 46% against the merge-base); reported
  to the PR's owner.
- **An undeclared contract change.** A Shared rebuild with an external writer process fails with the
  PR's `IOException` "Close other processes' database connections ...", which `docs/transaction-handles.md`
  does not declare; it stops half of the `transaction-interleavings` seeds on every PR tree (V-A2).

## Per-PR CI cost (measured)

Measured locally on the integrated branch (INTEGRATION report, 24 cores shared, load 11-30); hosted
runners differ.

| Item | dev | With the nets | Added |
| --- | --- | --- | --- |
| Job on every PR: oracle-smoke | - | 69 s of steps | +1 job (checkout and restore not measured) |
| Job on every PR: Concurrency models (Coyote) | - | 24 s incl. build | +1 job |
| Test leg, `engine-explorer` partition | - | 144-170 s | per leg (3 legs on the PR tier) |
| Test leg, `remaining` partition (sweep, graph, safety, PBT) | 32 s | 119-132 s | +87-100 s per leg |
| Whole net8.0 leg (sum of partitions) | 863 s | 1048-1399 s | about +185-270 s, noise-dominated |
| Fuzz smoke legs | core 71 s | core 69 s; teardown 78-83 s; concurrency explorer 112 s; shared contention 32 s | 3 new legs on their own runners |
| PR-selected fuzz, one line in `QueryExecutor.cs` | none | 152 s capped (355 s uncapped, 7 targets x 100) | dominated by `lifetime-chaos` x40 (150 s) and `teardown-faults` x30 (82 s) |
| PR-selected fuzz, a lock line in `SharedEngine.cs` | none | 163 s capped (897 s uncapped, 8 targets x 100) | dominated by `lifetime-chaos` x40 (158 s) and `transaction-interleavings` x40 (132 s); uncapped it was `shared-contention` x100 |
| PR-selected fuzz, all-targets fallback | none | 181-184 s capped (306 s uncapped, 50 targets x 30; paired rerun 224 s) | dominated by `lifetime-chaos` (108 s) |

Runner time added on the PR tier is about 13 minutes (the two new partitions on three legs); wall
clock grows by about 4.5 minutes when a test leg is the critical path. **Budget cap:** the
PR-selected job runs the explorer and multi-process targets at their smoke counts (`prCounts` in
`.github/safety/fuzz-obligations.json`: `lifetime-chaos` and `transaction-interleavings` 40,
`teardown-faults`, `chaos-maintenance`, `shared` and `snapshot` 30, `shared-contention` 5), one
concurrent run per count group. Measured with the workflow step verbatim (seed 2950080, 24 shared
cores, load 9-18): 355 s to 152 s (typical change), 897 s to 163 s (Shared change) and 306 s to 181-184 s
(all targets). A diff classified critical runs every target, so a Shared lock change costs the
all-targets time in CI. The 15-20 minute PR budget of the plan is met on these samples, but `shared-process` (217 s) and `engine-explorer` (170 s) sit near the 300 s
session limit on slower legs.

## Generator gaps (#3091)

The dimensions below were identified *after* seeing which rows were missed, so any proof built from
them against these commits is `tuned-after-fix`. Details and acceptance criteria are in
[#3091](https://github.com/litedb-org/LiteDB/issues/3091).

| # | Dimension | Rows it would reach |
| --- | --- | --- |
| 1 | Sustained reader streams during maintenance | 2 |
| 2 | Raw engine lifecycle (`ILiteEngine` directly, Dispose with queued lock waiters) | 3, 13 |
| 3 | Begin under held resources (reader, cursor, pin, legacy transaction) | 4 |
| 4 | Dispose during exception propagation (primary exception preserved) | 6 |
| 5 | Fatal error alongside and after a completed teardown, deferred report | 7 |
| 6 | Crash leftovers as start state | 8 |
| 7 | Test-only pause points inside the library (admission release, scope restore, core retirement) | 14, 19 |
| 8 | Storage-stream callbacks during close (same connection, peer, Dispose) | 17, 28 |
| 9 | Callbacks that begin work (transaction or handle; same connection, peer, other file) | 24, 26, 28 |
| 10 | Capability-gated known-finding exclusion and "programs run per side" in every proof | all PR trees before the #3072 merge |

Late-reader callback kinds (mutex-backed snapshots, pinned and FOR UPDATE readers, facades over a
caller-owned engine) would reach rows 18, 20, 21, 22 and 25; they extend dimensions 8 and 9.

## Defects found on current `dev`

Verified on a clean `dev` at `7b71bc4dd` and on the published packages (ISSUES report).

| Defect | Found by | Filing |
| --- | --- | --- |
| Direct: Dispose during Rebuild is undone (Rebuild reopens the engine); a second concurrent Dispose returns while the first is still closing | Coyote model (M5), chaos-maintenance | [#3093](https://github.com/litedb-org/LiteDB/issues/3093) |
| Shared: BeginTrans, failing write, BeginTrans, Commit leaves the writer mutex held | parallel property (M5) | [#3094](https://github.com/litedb-org/LiteDB/issues/3094) |
| Direct: Dispose racing an active operation releases page buffers in use (internal assertions, share count -1) | M5 model tests, explorer (M3), chaos-maintenance | [comment on #3010](https://github.com/litedb-org/LiteDB/issues/3010#issuecomment-5960401962) |
| Shared: a second concurrent Dispose returns while the first still holds files and mutex | chaos-maintenance (M2b) | [#3095](https://github.com/litedb-org/LiteDB/issues/3095) |
| `SharedEngine.Dispose`: a failing cleanup step skips the rest | teardown sweep (M2b) | [#3096](https://github.com/litedb-org/LiteDB/issues/3096) |
| `SortDisk.Dispose` leaves the `-tmp` scratch when closing its streams fails | teardown sweep (M2b) | [#3097](https://github.com/litedb-org/LiteDB/issues/3097) |
| `LiteDatabase.Dispose` on a stream database with an open transaction throws and leaves its engine open (regression since 5.0.21) | teardown sweep (M2b) | [#3098](https://github.com/litedb-org/LiteDB/issues/3098) |
| TIMEOUT pragma stale after opening with a non-empty WAL; later `db.Timeout` changes ignored | explorer (M3) | [#3099](https://github.com/litedb-org/LiteDB/issues/3099) |
| Shared: a peer call from a `FileStorage.Upload` source stream hangs instead of being turned down | explorer (M3) | [#3100](https://github.com/litedb-org/LiteDB/issues/3100) (related #3073) |
| FOR UPDATE reader disposed on another thread leaves the collection write lock held | M2, while instrumenting the wait-for graph | [#3101](https://github.com/litedb-org/LiteDB/issues/3101) |
| Two Direct connections to one file on Linux both open and corrupt the log | explorer (M3) | not filed: known as #2163 and #3041 |
| ENGINE_DISPOSED for an operation queued behind Rebuild is by design but undocumented | `conflict` fuzz target in the differential run (M4) | not filed: a `contracts.json` claim instead |

The quarantined `Issue2127_Tests.InsertItemBackToBack_Test` passes on `dev` (weekly re-run): a
candidate to restore, not a defect.

## Defects found in the nets

Each was found by running a net against the history, and fixed (or recorded) before this report:
the wait-for graph rooted finished threads and abandoned owners (`c26509cf0`); counted a cancellation
nobody requests as a bound (`fc83c4380`); latched a false cycle of the explorer's own coordination waits
(`358986530`). A lifetime-chaos replay instruction passed without running (`20889e0f1`).
`teardown-faults` picked cases in thread order (`6c6927433`). `ConnectionClean` used a fixed wall-clock
grace that failed under load (`4a9d95f71`, `aa74d0d11`, `5664965f5`). The mutation gate failed in
advisory mode, counted TESTING-only lines as blocking and let a non-C# test change widen its scope
(`a606f973d`, `ae8a2b596`, `c360ad0e3`). The differential run could not tell an exercised, unchanged
path from an unexercised one (`8975a3d18`, `b2abb588c`). The frozen handle adapter opens a second Direct
connection in six `handle-overlap` variants, which the PR's admission turns down; that stays recorded as
an adapter limitation of the frozen replay.

## Independence

`designed-from-invariant` means the net was not changed after its author read the fix; it does not
mean the author was blind to the defect class. The plan and its review were written by people who knew
every row, and some dimensions (the callback dimension, the Coyote alphabet's callback dependency, the
rebuild injection) were prescribed with those rows in mind. What each verifier saw before its generic
run:

| Verifier | Saw before the generic run | Tuning recorded |
| --- | --- | --- |
| M1, M2, M3, M2b (rows 16, 17) | the `dev` tree, which already contains #3072 and #3077 (fix file names, marker names, a few added lines while porting); no fix test or proof | M2 graph proof of row 16 marked partially tuned; row 17 sweep drivers written after reading #3077's test (`row17-teardown-sweep-tuned`) |
| M4 (rows 10, 11) | the fix's subject line and file list for row 11 | lint (b) trigger words widened after reading the deleted comment (`row11-lint-b`) |
| M5 (row 13) | the known-bad code for its model; the fix's two file names, and the fix's code when building the fix-side model | none after the runs |
| REPLAY | code and `docs/transaction-handles.md` at `39f6c6b0` and a one-page API card; no fix commit | 5 logged post-freeze adaptations, instrumentation only |
| V-waitgraph | fix subject lines in `git log`; no fix diff before the known-bad runs | row 13 probe written after reading the fix's test; a row 4 experiment, not adopted |
| V-pbt | docs and public API at the known-bad commits | the fail-fast rule from later documentation (`row12-parallel-property-tuned`) |
| V-A, V-A2, V-B, V-C, V-E1, V-E2 | fix subject lines (echoed by `git worktree add` or the brief); V-E1 also two fix internals named in the replay notes | row 5 transition scenario (follow-up item written from the fix's test), row 8 crash-leftover prior (subject line) |
| V-D | for row 10, the M4 lint record and report, which quote the fix's narrowed sentences; for row 11 nothing of the fix before the measurements | mutation scope narrowed to the introducing commit's files (`row11-mutation-pilot-narrowed`) |
| NET-FIXES | V-A and V-E2, which describe the row 23 shape | the cancellation rule (`later-69b0663a-wait-for-graph-cancellation-rule`) |

Coverage patch: designed from a different defect (#3071) and the PR's own documents, applied to both
sides; it changes no oracle. Tuned entries are separate ledger entries with level `tuned-after-fix`;
none of them makes an obligation in [implement-safely](rules/implement-safely.md) mandatory.

## Appendix: every ledger entry

<!-- BEGIN net-entries: generated by .github/scripts/render_net_proofs.py; do not edit by hand -->
| Row | Net | Level | Ind. | Class | Result | Time to yell | Reproduced | Proof |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | chaos-maintenance | generic | designed | 1 | **not-fired** | - | - | `row1-chaos-maintenance` |
| 1 | chaos-maintenance | generic | designed | 1 | **not-fired** | - | - | `row1-chaos-maintenance-coverage` |
| 1 | lifetime-chaos | generic | designed | 2 | **proven** | 495 s | K' 8/10 reruns (2 passes: N0 admitted before the close; schedule-depe… | `row1-lifetime-chaos` |
| 1 | lifetime-chaos | generic | designed | 2 | **proven** | 633 s | K' 404:1 10/10 and 808:1 10/10, F' 0/10 each | `row1-lifetime-chaos-coverage` |
| 1 | transaction-interleavings | generic | designed | 1 | **not-fired** | - | - | `row1-transaction-interleavings` |
| 1 | transaction-interleavings | generic | designed | 1 | **not-fired** | - | - | `row1-transaction-interleavings-coverage` |
| 1 | wait-for-graph (rule: an unrequested cancellation… | tuned | tuned | 2 | **not-fired** | - | - | `row1-wait-for-graph-cancellation-rule` |
| 2 | chaos-maintenance | generic | designed | 1 | **not-fired** | - | - | `row2-chaos-maintenance` |
| 2 | chaos-maintenance | generic | designed | 1 | **not-fired** | - | - | `row2-chaos-maintenance-coverage` |
| 2 | lifetime-chaos | generic | designed | 2 | **not-fired** | - | - | `row2-lifetime-chaos` |
| 2 | lifetime-chaos | generic | designed | 2 | **not-fired** | - | - | `row2-lifetime-chaos-coverage` |
| 2 | transaction-interleavings | generic | designed | 1 | **not-fired** | - | - | `row2-transaction-interleavings` |
| 2 | transaction-interleavings | generic | designed | 1 | **not-fired** | - | - | `row2-transaction-interleavings-coverage` |
| 3 | lifetime-chaos | generic | designed | 2 | **not-fired** | - | - | `row3-lifetime-chaos` |
| 3 | lifetime-chaos | generic | designed | 2 | **not-fired** | - | - | `row3-lifetime-chaos-coverage` |
| 3 | transaction-interleavings | generic | designed | 1 | **not-fired** | - | - | `row3-transaction-interleavings` |
| 3 | transaction-interleavings | generic | designed | 1 | **not-fired** | - | - | `row3-transaction-interleavings-coverage` |
| 3 | wait-for-graph | repro | designed | 2 | **fired-differently** | 15.1 s | fix's test at K'; F' 7/7 pass, no collection-lock cycle | `row3-wait-for-graph` |
| 3 | wait-for-graph (existing suite at known-bad) | generic | designed | 2 | **not-fired** | - | - | `row3-wait-for-graph-generic` |
| 4 | lifetime-chaos | generic | designed | 2 | **not-fired** | - | - | `row4-lifetime-chaos` |
| 4 | lifetime-chaos | generic | designed | 2 | **not-fired** | - | - | `row4-lifetime-chaos-coverage` |
| 4 | transaction-interleavings | generic | designed | 1 | **not-fired** | - | - | `row4-transaction-interleavings` |
| 4 | transaction-interleavings | generic | designed | 1 | **not-fired** | - | - | `row4-transaction-interleavings-coverage` |
| 4 | wait-for-graph | repro | designed | 2 | **not-fired** | - | - | `row4-wait-for-graph` |
| 4 | wait-for-graph (existing suite at known-bad) | generic | designed | 2 | **not-fired** | - | - | `row4-wait-for-graph-generic` |
| 5 | explorer matrix: handle-adapter scenarios | generic | designed | 1 | **not-fired** | - | - | `row5-explorer-handle-vectors` |
| 5 | explorer: handle-disposed-bound-object (transitio… | tuned | tuned | 1 | **proven** | 3 s | 32/48 vectors at K' (replay 9/9 same id), 0/48 at F' | `row5-explorer-transition` |
| 5 | lifetime-chaos | generic | designed | 2 | **not-fired** | - | - | `row5-lifetime-chaos` |
| 5 | parallel-property | generic | designed | 2 | **not-fired** | - | - | `row5-parallel-property` |
| 5 | transaction-interleavings | generic | designed | 1 | **not-fired** | - | - | `row5-transaction-interleavings` |
| 6 | explorer: handle access + FaultReached/FaultDispo… | generic | designed | 1 | **not-fired** | - | - | `row6-explorer-fault` |
| 6 | teardown-faults | generic | designed | 1 | **not-fired** | - | - | `row6-teardown-faults` |
| 6 | teardown-sweep | generic | designed | 1 | **not-fired** | - | - | `row6-teardown-sweep` |
| 7 | chaos-maintenance | generic | designed | 1 | **not-fired** | - | - | `row7-chaos-maintenance` |
| 7 | teardown-faults | generic | designed | 1 | **not-fired** | - | - | `row7-teardown-faults` |
| 7 | teardown-sweep | generic | designed | 1 | **not-fired** | - | - | `row7-teardown-sweep` |
| 7 | transaction-interleavings | generic | designed | 1 | **not-fired** | - | - | `row7-transaction-interleavings` |
| 8 | teardown-sweep + crash-leftover prior (Quiescent … | tuned | tuned | 1 | **proven** | 74 s | 48 baselines at K' (LiteDatabase.Dispose theory 2/2), 0 at F' apart f… | `row8-crash-leftovers` |
| 8 | fuzz target sort | generic | designed | 1 | **not-fired** | - | - | `row8-sort` |
| 8 | explorer: spilled-reader kind + ScratchLive | generic | designed | 1 | **not-fired** | - | - | `row8-spilled-reader` |
| 8 | teardown-faults | generic | designed | 1 | **not-fired** | - | - | `row8-teardown-faults` |
| 8 | teardown-sweep | generic | designed | 1 | **not-fired** | - | - | `row8-teardown-sweep` |
| 9 | teardown-faults | generic | designed | 1 | **proven** | 270 s | --seed 5 --count 5: K' 3/3 same id, F' 3/3 pass | `row9-teardown-faults` |
| 9 | teardown-sweep | generic | designed | 1 | **proven** | 2 s | ClosePin theory K' 3/3 (same 4 cases), F' 3/3 none | `row9-teardown-sweep` |
| 10 | differential run (normalized outcomes + intended-… | generic | designed | 2 | **not-fired** | - | - | `row10-differential` |
| 10 | lint (c) doc claims | generic | designed | 1 | **fired-differently** | 0.09 s | - | `row10-lint-c` |
| 11 | contention metrics (F' as baseline, K' as candida… | generic | designed | 3 | **not-fired** | - | - | `row11-contention-benchmark` |
| 11 | contention metrics (merge-base as baseline) | generic | designed | 3 | **fired-differently** | - | base -> K' 2/2 on overtaking (p99 1/2: load noise); 70429cb6^ -> 7042… | `row11-contention-metrics` |
| 11 | differential run (normalized outcomes) | generic | designed | 2 | **not-fired** | - | - | `row11-differential` |
| 11 | lint (a) polling | generic | designed | 1 | **proven** | 0.1 s | - | `row11-lint-a` |
| 11 | lint (b) deleted invariant comments | tuned | tuned | 1 | **proven** | 0.15 s | - | `row11-lint-b` |
| 11 | mutation pilot (configured scope) | generic | designed | 2 | **harness-error** | - | - | `row11-mutation-pilot` |
| 11 | mutation pilot (narrowed scope) | tuned | tuned | 2 | **not-fired** | - | - | `row11-mutation-pilot-narrowed` |
| 12 | parallel-property (handle access kind) | generic | designed | 2 | **not-fired** | - | - | `row12-parallel-property` |
| 12 | parallel-property (handle kind, fail-fast self-wa… | tuned | tuned | 2 | **proven** | 2.9 s | 20/20 replays, 20/20 shrunk reproductions | `row12-parallel-property-tuned` |
| 12 | wait-for-graph | repro | designed | 2 | **proven** | 0.161 s | 4/4 failing cases latch self-wait; F' 6/6 pass, zero findings | `row12-wait-for-graph` |
| 12 | wait-for-graph (existing suite at known-bad) | generic | designed | 2 | **not-fired** | - | - | `row12-wait-for-graph-generic` |
| 13 | Coyote lifetime model | model | designed | 1 | **proven** | 0.8 s | replay of the recorded trace reproduces the identical assertion; F' c… | `row13-coyote` |
| 13 | lifetime-chaos | generic | designed | 2 | **not-fired** | - | - | `row13-lifetime-chaos` |
| 13 | lifetime-chaos | generic | designed | 2 | **not-fired** | - | - | `row13-lifetime-chaos-coverage` |
| 13 | transaction-interleavings | generic | designed | 1 | **not-fired** | - | - | `row13-transaction-interleavings` |
| 13 | transaction-interleavings | generic | designed | 1 | **not-fired** | - | - | `row13-transaction-interleavings-coverage` |
| 13 | wait-for-graph | repro | tuned | 2 | **proven** | 0.029 s | K' fires; F' 4/4 pass, no unbounded-cycle | `row13-wait-for-graph` |
| 13 | wait-for-graph (existing suite at known-bad) | generic | designed | 2 | **not-fired** | - | - | `row13-wait-for-graph-generic` |
| 14 | composed re-run (corpus + every new target's smok… | generic | designed | 2 | **not-fired** | - | - | `row14-composed-rerun` |
| 14 | explorer matrix (access=handle) + transaction-int… | generic | designed | 1 | **not-fired** | - | - | `row14-explorer` |
| 14 | lifetime-chaos | generic | designed | 2 | **not-fired** | - | - | `row14-lifetime-chaos` |
| 14 | parallel-property (handle access kind) | generic | designed | 2 | **not-fired** | - | - | `row14-parallel-property` |
| 14 | wait-for graph on the fix's own tests | repro | designed | 2 | **not-fired** | - | - | `row14-reproduction` |
| 15 | chaos-maintenance | generic | designed | 1 | **not-fired** | - | - | `row15-chaos-maintenance` |
| 15 | composed re-run (corpus + every new target's smok… | generic | designed | 2 | **not-fired** | - | - | `row15-composed-rerun` |
| 15 | explorer matrix: Ownership (Shared handle and leg… | generic | designed | 1 | **not-fired** | - | - | `row15-explorer-ownership` |
| 15 | lifetime-chaos | generic | designed | 2 | **not-fired** | - | - | `row15-lifetime-chaos` |
| 15 | ownership oracle self-tests (LiteDB.Fuzz.Tests) | generic | designed | 1 | **not-fired** | - | - | `row15-ownership-oracle-xunit` |
| 15 | wait-for graph on the fix's own tests | repro | designed | 2 | **not-fired** | - | - | `row15-reproduction` |
| 15 | teardown-faults | generic | designed | 2 | **proven** | 9 s | - | `row15-teardown-faults` |
| 15 | teardown-sweep | generic | designed | 2 | **proven** | 2.2 s | K' 10/10 (load 8.5-135), F' 0/10 | `row15-teardown-sweep` |
| 16 | explorer targets transaction-interleavings + life… | generic | designed | 1 | **proven** | 21.3 s | class 1 minimization replays the same id at count 4; chaos replay at … | `row16-explorer` |
| 16 | wait-for-graph | repro | tuned | 2 | **proven** | 0.53 s | 28/28 hanging cases latch; F' 40/40 pass, zero findings | `row16-wait-for-graph` |
| 16 | wait-for-graph (existing suite at known-bad) | generic | designed | 2 | **not-fired** | - | - | `row16-wait-for-graph-generic` |
| 16 | wait-for-graph | repro | tuned | 2 | **proven** | - | 28/28 hanging cases; F' 40/40 pass, zero findings | `row16-wait-for-graph-rule` |
| 17 | explorer targets transaction-interleavings + life… | generic | designed | 1 | **not-fired** | - | - | `row17-explorer` |
| 17 | ownership | repro | designed | 2 | **proven** | - | 4 of 24 cases, 3/3 runs; F' 24/24 pass | `row17-ownership` |
| 17 | teardown-sweep | generic | designed | 2 | **not-fired** | - | - | `row17-teardown-sweep` |
| 17 | teardown-sweep (self-callback drivers) | tuned | tuned | 2 | **proven** | - | 4/4 sweep runs, fuzz 2/2 | `row17-teardown-sweep-tuned` |
| 17 | wait-for-graph | repro | designed | 2 | **fired-differently** | - | F' 82/82 pass, zero findings | `row17-wait-for-graph` |
| 18 | lifetime-chaos | generic | designed | 2 | **not-fired** | - | - | `later-9199637e-lifetime-chaos` |
| 18 | wait-for graph on the fix's own tests | repro | designed | 2 | **not-fired** | - | - | `later-9199637e-reproduction` |
| 18 | teardown-faults | generic | designed | 1 | **not-fired** | - | - | `later-9199637e-teardown-faults` |
| 18 | teardown-sweep | generic | designed | 2 | **not-fired** | - | - | `later-9199637e-teardown-sweep` |
| 18 | transaction-interleavings | generic | designed | 1 | **not-fired** | - | - | `later-9199637e-transaction-interleavings` |
| 18 | wait-for-graph | generic | designed | 2 | **not-fired** | - | - | `later-9199637e-wait-for-graph` |
| 19 | lifetime-chaos | generic | designed | 2 | **not-fired** | - | - | `later-3166d49c-lifetime-chaos` |
| 19 | wait-for graph on the fix's own tests | repro | designed | 2 | **not-fired** | - | - | `later-3166d49c-reproduction` |
| 19 | teardown-faults | generic | designed | 1 | **not-fired** | - | - | `later-3166d49c-teardown-faults` |
| 19 | teardown-sweep | generic | designed | 2 | **not-fired** | - | - | `later-3166d49c-teardown-sweep` |
| 19 | transaction-interleavings | generic | designed | 1 | **not-fired** | - | - | `later-3166d49c-transaction-interleavings` |
| 19 | wait-for-graph | generic | designed | 2 | **not-fired** | - | - | `later-3166d49c-wait-for-graph` |
| 20 | lifetime-chaos | generic | designed | 2 | **not-fired** | - | - | `later-c20deb01-lifetime-chaos` |
| 20 | wait-for graph on the fix's own tests | repro | designed | 2 | **not-fired** | - | - | `later-c20deb01-reproduction` |
| 20 | teardown-faults | generic | designed | 1 | **not-fired** | - | - | `later-c20deb01-teardown-faults` |
| 20 | teardown-sweep | generic | designed | 2 | **not-fired** | - | - | `later-c20deb01-teardown-sweep` |
| 20 | transaction-interleavings | generic | designed | 1 | **not-fired** | - | - | `later-c20deb01-transaction-interleavings` |
| 20 | wait-for-graph | generic | designed | 2 | **not-fired** | - | - | `later-c20deb01-wait-for-graph` |
| 21 | lifetime-chaos | generic | designed | 2 | **not-fired** | - | - | `later-d9a5bfe6-lifetime-chaos` |
| 21 | wait-for graph on the fix's own tests | repro | designed | 2 | **proven** | 0.005 s | 6/6 cases, 10/10 runs at K'; F' 8/8 pass, 10/10 runs | `later-d9a5bfe6-reproduction` |
| 21 | teardown-faults | generic | designed | 1 | **not-fired** | - | - | `later-d9a5bfe6-teardown-faults` |
| 21 | teardown-sweep | generic | designed | 2 | **not-fired** | - | - | `later-d9a5bfe6-teardown-sweep` |
| 21 | transaction-interleavings | generic | designed | 1 | **not-fired** | - | - | `later-d9a5bfe6-transaction-interleavings` |
| 21 | wait-for-graph | generic | designed | 2 | **not-fired** | - | - | `later-d9a5bfe6-wait-for-graph` |
| 22 | lifetime-chaos | generic | designed | 2 | **not-fired** | - | - | `later-f8de2bb9-lifetime-chaos` |
| 22 | wait-for graph on the fix's own tests | repro | designed | 2 | **not-fired** | - | - | `later-f8de2bb9-reproduction` |
| 22 | teardown-faults | generic | designed | 1 | **not-fired** | - | - | `later-f8de2bb9-teardown-faults` |
| 22 | teardown-sweep | generic | designed | 2 | **not-fired** | - | - | `later-f8de2bb9-teardown-sweep` |
| 22 | transaction-interleavings | generic | designed | 1 | **not-fired** | - | - | `later-f8de2bb9-transaction-interleavings` |
| 22 | wait-for-graph | generic | designed | 2 | **not-fired** | - | - | `later-f8de2bb9-wait-for-graph` |
| 23 | lifetime-chaos | generic | designed | 2 | **proven** | 192 s | K' 10/10 each, F' 0/10 each | `later-69b0663a-lifetime-chaos` |
| 23 | teardown-faults | generic | designed | 1 | **not-fired** | - | - | `later-69b0663a-teardown-faults` |
| 23 | teardown-sweep | generic | designed | 2 | **not-fired** | - | - | `later-69b0663a-teardown-sweep` |
| 23 | transaction-interleavings | generic | designed | 1 | **not-fired** | - | - | `later-69b0663a-transaction-interleavings` |
| 23 | wait-for-graph | generic | designed | 2 | **not-fired** | - | - | `later-69b0663a-wait-for-graph` |
| 23 | wait-for-graph (rule: an unrequested cancellation… | tuned | tuned | 2 | **proven** | - | 6/6 replays at K' (2947:5 x3, 101:7 x3); F' 6/6 pass, zero findings | `later-69b0663a-wait-for-graph-cancellation-rule` |
| 24 | lifetime-chaos | generic | designed | 2 | **not-fired** | - | - | `later-2a1549a6-lifetime-chaos` |
| 24 | teardown-faults | generic | designed | 1 | **not-fired** | - | - | `later-2a1549a6-teardown-faults` |
| 24 | teardown-sweep | generic | designed | 2 | **not-fired** | - | - | `later-2a1549a6-teardown-sweep` |
| 24 | transaction-interleavings | generic | designed | 1 | **not-fired** | - | - | `later-2a1549a6-transaction-interleavings` |
| 24 | wait-for-graph | generic | designed | 2 | **not-fired** | - | - | `later-2a1549a6-wait-for-graph` |
| 25 | lifetime-chaos | generic | designed | 2 | **not-fired** | - | - | `later-c172623f-lifetime-chaos` |
| 25 | teardown-faults | generic | designed | 1 | **not-fired** | - | - | `later-c172623f-teardown-faults` |
| 25 | teardown-sweep | generic | designed | 2 | **not-fired** | - | - | `later-c172623f-teardown-sweep` |
| 25 | transaction-interleavings | generic | designed | 1 | **not-fired** | - | - | `later-c172623f-transaction-interleavings` |
| 25 | wait-for-graph | generic | designed | 2 | **not-fired** | - | - | `later-c172623f-wait-for-graph` |
| 26 | lifetime-chaos | generic | designed | 2 | **not-fired** | - | - | `later-ab8aa1ed-lifetime-chaos` |
| 26 | teardown-faults | generic | designed | 1 | **not-fired** | - | - | `later-ab8aa1ed-teardown-faults` |
| 26 | teardown-sweep | generic | designed | 2 | **not-fired** | - | - | `later-ab8aa1ed-teardown-sweep` |
| 26 | transaction-interleavings | generic | designed | 1 | **not-fired** | - | - | `later-ab8aa1ed-transaction-interleavings` |
| 26 | wait-for-graph | generic | designed | 2 | **not-fired** | - | - | `later-ab8aa1ed-wait-for-graph` |
| 27 | lifetime-chaos | generic | designed | 2 | **proven** | 398 s | K' 9/10, F' 0/10 | `later-c6e6c848-lifetime-chaos` |
| 27 | teardown-faults | generic | designed | 1 | **not-fired** | - | - | `later-c6e6c848-teardown-faults` |
| 27 | teardown-sweep | generic | designed | 2 | **not-fired** | - | - | `later-c6e6c848-teardown-sweep` |
| 27 | transaction-interleavings | generic | designed | 1 | **proven** | 138 s | vector replay K' 3/3, F' 0/3 | `later-c6e6c848-transaction-interleavings` |
| 27 | wait-for-graph | generic | designed | 2 | **proven** | 5 s | every unpatched-K' Shared peer deadline and 3/3 vector replays; none … | `later-c6e6c848-wait-for-graph` |
| 28 | lifetime-chaos | generic | designed | 2 | **not-fired** | - | - | `later-0d7ea1ee-lifetime-chaos` |
| 28 | teardown-faults | generic | designed | 1 | **not-fired** | - | - | `later-0d7ea1ee-teardown-faults` |
| 28 | teardown-sweep | generic | designed | 2 | **not-fired** | - | - | `later-0d7ea1ee-teardown-sweep` |
| 28 | transaction-interleavings | generic | designed | 1 | **not-fired** | - | - | `later-0d7ea1ee-transaction-interleavings` |
| 28 | wait-for-graph | generic | designed | 2 | **not-fired** | - | - | `later-0d7ea1ee-wait-for-graph` |
| 29 | lifetime-chaos | generic | designed | 2 | **not-fired** | - | - | `later-56c8680a-lifetime-chaos` |
| 29 | teardown-faults | generic | designed | 1 | **not-fired** | - | - | `later-56c8680a-teardown-faults` |
| 29 | teardown-sweep | generic | designed | 2 | **fired-differently** | 64 s | theory K' 10/10, F' 0/10 | `later-56c8680a-teardown-sweep` |
| 29 | transaction-interleavings | generic | designed | 1 | **not-fired** | - | - | `later-56c8680a-transaction-interleavings` |
| 29 | wait-for-graph | generic | designed | 2 | **proven** | 2 s | every K' run, 10/10 theory reruns; none at F' | `later-56c8680a-wait-for-graph` |

152 entries. Rendered from `.github/safety/net-proofs.json`; refresh with `python .github/scripts/render_net_proofs.py --write <file>`.
<!-- END net-entries -->
