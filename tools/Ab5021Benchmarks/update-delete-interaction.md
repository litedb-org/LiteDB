# Update/delete CPU interaction diagnosis

The hosted counterfactual identifies inherited heap/GC state as a substantial cause of the net8 update timing interaction. No extra delete check executes on the timed update path. No library fix or gate-threshold change is justified by this evidence. This investigation does **not** establish the exact cause of the historical untraced full comparisons or qualify phase1 promotion.

## Source trace

`Workload.Run` calls `col.Update(updates)` within BeginTrans/Commit. Typed Update lazily maps documents and calls engine Update, which uses primary-key Find, DataService.Update and secondary-key comparison. It does not call DeleteMany, PrimaryKeyDeleteRange or IndexRange.DeletePositions.

The complete delete diff against phase1 changes only Delete.cs, adds PrimaryKeyDeleteRange, and adds a separate DeletePositions iterator to IndexRange. Existing IndexRange.Run and its fields are unchanged. Integrated-vs-update product diff equals delete-vs-phase1 product diff. There is no added QueryExecutor, snapshot or shared index-service update-path check. Metadata/JIT layout effects cannot be excluded without native traces, but no sustained CPU regression survives the heap-normalized comparisons.

Warmup executes delete before the measured workload. The historical focused mechanism also skips SmallTransactions, Checkpoint and DurableWriters in warmup when those scenarios are not selected. That changes the heap state inherited by measured update. The second diagnostic series preserves **all full-gate warmup actions**, while retaining update-only reporting. No measured update operation, correctness assertion, rounds, production mode or threshold changes.

## Method and identities

GitHub Actions only; Windows2025/Ubuntu24.04, net8.0 (.NET8.0.33), four reported CPUs, production Release/TestingEnabled=false, TieredCompilation=0, six interleaved rounds per configuration. Fresh process and GUID temp directory per variant/configuration/round; same workload source for all libraries in a run. Subsets disable all gate verdicts. Bounds in raw tables are observed extrema, not confidence intervals. Hosted machine differences preclude comparing absolute times between separate runs as causal evidence.

Libraries:
- phase1: dac4520a6c0d811b80919a117ba8b88676f5a3ac
- update-only: 3a2131866b77cc4b96a0611083d2742770922c3b
- delete-only: 9b8d71af0790bd8c86c6b216f1e9fb9131b1d51b
- integrated: 96bac2b7f4ecaf64fd3d950f89beacadecabea8a

Harnesses: fb9892d0c (initial counters/normalization), 418f9c927 (original uninstrumented Workload.cs), cdb0f3c26 (counters/normalization with full warmup). All26 hosted runs/52 jobs completed SUCCESS. Artifact analysis validates exact source identities, actual runtime, complete rounds and stable binary identity for instrumented runs. Raw source hashes, environment metadata, rounds, counters and checksums are retained under `/tmp/litedb-proto/phases/p1-cpu-interact-evidence/`.

## OS-cache update after/phase1

Pairs are repeat1 / repeat2, ratio of medians; lower is faster.

| Warmup | Library | Collect before timer | Windows net8 | Linux net8 |
|---|---|---|---|---|
| Original focused | phase1 | False | 1.0142 / 0.9617 | 0.9957 / 1.0154 |
| Original focused | phase1 | True | 0.9914 / 0.9953 | 0.9874 / 1.0006 |
| Original focused | update | False | 0.9268 / 0.9016 | 0.9285 / 0.8949 |
| Original focused | update | True | 0.9052 / 0.9326 | 0.8936 / 0.8835 |
| Original focused | delete | False | 1.0105 / 1.0068 | 1.0622 / 1.0481 |
| Original focused | delete | True | 0.9922 / 1.0360 | 0.9670 / 1.0054 |
| Original focused | integrated | False | 0.9542 / 0.9516 | 0.7962 / 1.0118 |
| Original focused | integrated | True | 0.9119 / 0.8933 | 0.8769 / 0.8949 |
| Full gate | delete | False | 1.0051 / 1.0200 | 1.1127 / 1.1612 |
| Full gate | delete | True | 0.9824 / 0.9672 | 1.0016 / 1.0244 |
| Full gate | integrated | False | 0.9431 / 1.0667 | 1.0631 / 0.9436 |
| Full gate | integrated | True | 0.8756 / 0.9054 | 0.9014 / 0.9427 |

The two original-source, uninstrumented integrated focused controls also improved OS-cache update: Windows0.9366/0.9123x, Linux0.8869/0.9710x. Therefore the initial focused improvement is not explained solely by adding the counters. One default-config Windows control is1.420x, repeat0.945x; retained as a non-repeated diagnostic outlier, never called a gate pass.

## Mechanism and limits

Full-warmup Linux delete-only is1.113/1.161x without normalization,1.002/1.024x after pre-timer full collection/finalizer draining. Timed allocations are identical after normalization (~207.54MB for phase1/delete); no extra update work is present in the delete diff. Equal collection counts in some normal samples do not establish equal collection cost or survivor state.

Full-warmup integrated normal Windows repeat2 is1.067x. Baseline rounds are72–76ms, all GC=[12,1,0]. Candidate rounds with GC=[12,3,1] take87.52/96.13/96.48ms; those without gen2 take64.02/67.35/70.56ms. First repeat has the same pattern: gen2 rounds141.61/153.95ms versus107.77–118.46ms without gen2. All normalized integrated rounds have no gen1/gen2; median ratios are0.876/0.905x Windows and0.901/0.943x Linux. Integrated and update-only save ~10.16MB per timed update (~207.54→197.38MB).

These controlled interventions support inherited heap/GC placement as a substantial contributor. They do not measure exact GC pause duration or CPU samples and do not prove the historical full-run slowdown had only this cause. Full-warmup integrated normal Linux is1.063/0.944x, Windows0.943/1.067x; the historical repeated1.08–1.19x slowdown is not exactly reproduced across both repeats. No native JIT/inlining diagnosis is claimed. A/A controls and observed bounds are retained rather than declaring every residual difference noise.

## Benchmark isolation proposal

Keep gate thresholds and current full gate unchanged. For a separate diagnostic, always preserve full warmup independent of selected output. Report allocation bytes, gen0/1/2 deltas and GC pause duration around each timer. Run an optional collection/finalizer drain after scenario setup and before the timer, report both raw and normalized results, and retain correctness checks outside and inside the timer as currently defined. A dedicated process per scenario with comparable setup and fully counterbalanced before/after order would further separate previous-scenario garbage, code warmup and host order. Do not use no-GC regions or silently substitute normalized numbers for gate results.

## Disposition

No library changes, no new safety claims, no standard/safety/full-gate reruns after a product fix because no product fix was made. Existing exact-integrated-head standard and targeted safety evidence remains recorded in the integration log. No phase1 push or PR body edit: the historical full acceptance matrix remains unmet, especially Linux net10 update. Delete was not remeasured here; historical full integrated delete ratios remain Windows net8 0.417–0.437, Linux net8 0.432–0.447, Windows net10 0.390–0.402, Linux net10 0.400–0.419. Historical integrated update net10 remains Windows0.833–0.846, Linux1.006–1.007. None of those historical numbers are new qualification evidence.

## Hosted runs

### Initial matrix

- phase1, repeat1, collect=False: [run42358010173](https://github.com/litedb-org/LiteDB/actions/runs/42358010173)
- update, repeat1, collect=False: [run42358012319](https://github.com/litedb-org/LiteDB/actions/runs/42358012319)
- delete, repeat1, collect=False: [run42358014844](https://github.com/litedb-org/LiteDB/actions/runs/42358014844)
- integrated, repeat1, collect=False: [run42358017110](https://github.com/litedb-org/LiteDB/actions/runs/42358017110)
- phase1, repeat1, collect=True: [run42358019512](https://github.com/litedb-org/LiteDB/actions/runs/42358019512)
- update, repeat1, collect=True: [run42358022141](https://github.com/litedb-org/LiteDB/actions/runs/42358022141)
- delete, repeat1, collect=True: [run42358024247](https://github.com/litedb-org/LiteDB/actions/runs/42358024247)
- integrated, repeat1, collect=True: [run42358026463](https://github.com/litedb-org/LiteDB/actions/runs/42358026463)
- phase1, repeat2, collect=False: [run42358028787](https://github.com/litedb-org/LiteDB/actions/runs/42358028787)
- update, repeat2, collect=False: [run42358030948](https://github.com/litedb-org/LiteDB/actions/runs/42358030948)
- delete, repeat2, collect=False: [run42358033354](https://github.com/litedb-org/LiteDB/actions/runs/42358033354)
- integrated, repeat2, collect=False: [run42358035634](https://github.com/litedb-org/LiteDB/actions/runs/42358035634)
- phase1, repeat2, collect=True: [run42358037982](https://github.com/litedb-org/LiteDB/actions/runs/42358037982)
- update, repeat2, collect=True: [run42358040532](https://github.com/litedb-org/LiteDB/actions/runs/42358040532)
- delete, repeat2, collect=True: [run42358042924](https://github.com/litedb-org/LiteDB/actions/runs/42358042924)
- integrated, repeat2, collect=True: [run42358045210](https://github.com/litedb-org/LiteDB/actions/runs/42358045210)
### Full warmup

- delete, repeat1, collect=False: [run42358842834](https://github.com/litedb-org/LiteDB/actions/runs/42358842834)
- integrated, repeat1, collect=False: [run42358845010](https://github.com/litedb-org/LiteDB/actions/runs/42358845010)
- delete, repeat1, collect=True: [run42358847179](https://github.com/litedb-org/LiteDB/actions/runs/42358847179)
- integrated, repeat1, collect=True: [run42358849333](https://github.com/litedb-org/LiteDB/actions/runs/42358849333)
- delete, repeat2, collect=False: [run42358851259](https://github.com/litedb-org/LiteDB/actions/runs/42358851259)
- integrated, repeat2, collect=False: [run42358853103](https://github.com/litedb-org/LiteDB/actions/runs/42358853103)
- delete, repeat2, collect=True: [run42358854953](https://github.com/litedb-org/LiteDB/actions/runs/42358854953)
- integrated, repeat2, collect=True: [run42358857114](https://github.com/litedb-org/LiteDB/actions/runs/42358857114)

Original-source controls: [42358785595](https://github.com/litedb-org/LiteDB/actions/runs/42358785595), [42358788356](https://github.com/litedb-org/LiteDB/actions/runs/42358788356).
