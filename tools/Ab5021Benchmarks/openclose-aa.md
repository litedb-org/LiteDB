# P1-OPENCLOSE-AA research

Orchestrator instructed unchanged-source controls for the reported open/close
regression. No product changes or product fix are proposed from this evidence.

## Requested A/A controls

Both sides: `dac4520a6c0d811b80919a117ba8b88676f5a3ac`.
Harness: `3a2131866b77cc4b96a0611083d2742770922c3b`, exactly the focused
diagnostic [42351087122](https://github.com/litedb-org/LiteDB/actions/runs/42351087122).
Dispatch: `benchmark-gate.yml`, `comparison_scenarios=open_close_20x`,
`candidate_ref=baseline_ref=dac4520a6c0d811b80919a117ba8b88676f5a3ac`,
`slow_comparison=false`. Windows/Linux: six rounds; macOS: ten.
Both runs completed successfully on all six existing OS/runtime jobs.

- [Run 1](https://github.com/litedb-org/LiteDB/actions/runs/42352657391)
- [Run 2](https://github.com/litedb-org/LiteDB/actions/runs/42352659160)

After/before median ratios for `dev-os-cache`; brackets are observed
`min(after)/max(before)` to `max(after)/min(before)`, not confidence intervals.

| Runtime | Run 1 | Run 2 |
| --- | --- | --- |
| Windows net8 | 1.022 [0.909, 1.219] | 0.995 [0.858, 1.058] |
| Windows net10 | 0.988 [0.823, 1.137] | 0.994 [0.960, 1.032] |
| macOS net8 | 1.037 [0.313, 1.282] | 1.332 [0.446, 2.678] |
| Linux net8 | 1.004 [0.941, 1.048] | 1.222 [0.937, 1.390] |

The unchanged-source macOS net8 control reproduces a >1.3x median swing.
Windows controls are clean; macOS noise alone cannot explain Windows.
Durable-default median ratios (run1;run2): Windows8 1.000;0.994,
Windows10 1.001;1.004, macOS8 1.068;0.926, Linux8 1.009;1.049.

## What the benchmark measures

`Workload.Run` creates a fresh GUID-named file, inserts 100k documents, queries,
updates 10k, deletes 10k, verifies and checkpoints. It then times 20 iterations of
`new LiteDatabase`, `GetCollectionNames().Single()`, and disposal. The collection
name check runs a read-only `$cols` query. It does not update or insert.
Scenario selection filters samples, not these setup actions. Each process first
executes a scaled warmup including update/open/query/dispose; tiered compilation
is disabled. This is warm post-workload open/query/close, not cold first JIT.
The measured file has just been written/read/checkpointed; no OS-cache eviction
occurs, and before/after never share the same file.

`comparison_order` rotates `[v5,before,after]` and reverses configuration order by
parity. Candidate is not always second, but variants are not fully balanced:
before runs ahead of after in 4/6 rounds (7/10 on macOS). The runner's ABBA comment
does not accurately describe this pair. Small position groups show no universal
position effect; imbalance is a design weakness, not an established cause.

## Product diff inspection

`dac4520a6..3a2131866` changes `UpdateDocument`, the null-membership check in
`IndexService.DeleteList`, and adds `UpdateIndexKeys`, an operation-local struct.
There are no added static initializers, readonly static tables, ThreadStatic
fields, pools, engine instance fields or constructor changes. Its List<ValueTuple>
spill is instantiated only by update operations. No harness change is in this diff;
remaining changes are tests, safety registration and update-safety CI.
`DeleteList` is also reachable during legacy index migration, but these newly
created current-format fixtures require no legacy migration.

## Phase diagnostic

[42353410291](https://github.com/litedb-org/LiteDB/actions/runs/42353410291)
completed all six jobs successfully on diagnostic harness `48f57b244`.
Both libraries use identical diagnostic source. It adds per-open timestamps,
GC collection counts and allocated-byte deltas, with the original checks retained.

- Linux8 OS-cache: 3.194 [2.305,3.990]. Baseline had no timed collections;
  candidate had gen0+gen1 collections in all six rounds. Timed allocation was
  about 3.12MB on both sides. The slow phase moved between construction, query
  and disposal, consistent with GC placement rather than added work in one phase.
- Windows8 OS-cache: 0.918 [0.694,0.961]. Baseline collected in all six rounds;
  candidate never collected. Both sides allocated about 5.67MB during the timer.
- Windows10 OS-cache: 1.007 [0.869,1.048]. Neither side collected in any round;
  the original >1.4x regression did not reproduce.
- macOS8: 1.010, neither side collected; macOS10: 0.747, baseline collected in
  every round, candidate in only one.

These results identify a scenario attribution artifact: update allocation changes
alter the GC state inherited by a later timer, producing either direction of
apparent open/close change. MacOS also exhibits unchanged-source host variability.
There is no evidence of an added open initialization cost. The original Windows10
runs have no phase/GC trace, so their exact delay cannot be retrospectively proven.

## GC-state counterfactual

[42353953036](https://github.com/litedb-org/LiteDB/actions/runs/42353953036)
completed all six jobs successfully on harness `574fa698c` with the same per-phase diagnostics and optional forced
full collection/finalizer drain immediately before the timer. This is a diagnostic
intervention, not a suggested product change or production benchmark replacement.

Linux8 OS-cache falls from 3.194 to 1.002 [0.953,1.047]; neither side collects
during the normalized timer. Windows8 is 1.028 [0.868,1.430], Windows10
1.003 [0.891,1.104], also with no timed collections. This controlled intervention
supports inherited GC placement as the source of the demonstrated net8 gaps.
MacOS8 is 0.911 [0.478,2.523], macOS10 0.968 [0.630,1.300]; neither side
collects in any normalized round. MacOS still has wide host-noise bounds.
This does not prove the exact cause of the historical untraced Windows10 runs.

## Proposed harness correction

Prepare an equivalent persisted fixture outside the measured child and isolate
open/query/dispose from update setup. State explicitly whether the target is cold
startup, warm reopen, or a complete workload. Record GC deltas and enough iterations
to distinguish steady-state costs from a single collection; use pre-timer collection
only as a diagnostic, retaining separate whole-workload measurements that include GC.
Fully counterbalance before/after order and v5 placement. No product GC calls or
storage changes are warranted. The opt-in controls in this scratch branch are
research diagnostics, not a replacement benchmark gate.

Raw hosted artifacts, full per-config medians/min/max, assembly hashes, runtime
identities, round orders, and analysis scripts are retained in
`/tmp/litedb-proto/phases/p1-openclose-aa-evidence/` with the incremental log at
`/tmp/litedb-proto/phases/p1-openclose-aa.md`. No local benchmarks were run.
