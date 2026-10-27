# Hosted LiteDB 5.0.21 comparison and Phase 1 gate

Run measurements only on GitHub Actions. Both the Python orchestrator and C#
entry point reject local measurements. Local builds and synthetic Python tests
are allowed; do not run the workload locally.

This ports the shared-source harness from
[`JKamsker/LiteDB` at `6f4ae96ce`](https://github.com/JKamsker/LiteDB/tree/6f4ae96ce/tools/Ab5021Benchmarks).
Its original six-platform evidence is
[run 40422881629](https://github.com/JKamsker/LiteDB/actions/runs/40422881629).
There is no benchmark dependency in the solution or production library.

## Running on CI

The optional **Phase 1 benchmark gate** workflow runs on `workflow_dispatch`, or
on PR open/reopen/update/label events when the PR has the `benchmark` label. It
checks out the PR head's own harness and library with a read-only token. It does
not require the harness to have merged into the base first. Do not configure this
workflow as a required check.

For dispatch, select the branch containing the harness. Leave inputs empty to
measure that branch's library. To measure a separate library without changing
the harness, supply `candidate_repository` and `candidate_ref`; use a full SHA
for repeatable evidence. For example:

```sh
gh workflow run benchmark-gate.yml -R JKamsker/LiteDB --ref v6/bench-gate \
  -f candidate_repository=litedb-org/LiteDB -f candidate_ref=<origin/dev SHA>
```

The fork-only `v6/bench-gate` push trigger bootstraps the new workflow without
changing a default branch. It measures that scratch branch's library; compare
its `LiteDB/` and build props/targets to the intended dev SHA before pushing.
Only use the fork for initial validation. The integration branch is
`v6/p1-bench-gate`; no PR is needed for this step.

## Same-job before/after comparison

Set `baseline_ref` to build a third library variant, from the same repository as
`candidate_ref` (`candidate_repository` applies to both). Pin both refs to full
SHAs. The harness always comes from the workflow branch. For example:

```sh
gh workflow run benchmark-gate.yml -R JKamsker/LiteDB --ref v6/p1-fix-gate-compare \
  -f candidate_repository=litedb-org/LiteDB \
  -f baseline_ref=a2664ea415e1e574bc5c8f28cb0c484c1bb898c7 \
  -f candidate_ref=0e98a80c754d30c4b1bb2ae2f298da95ee9633f6 \
  -f comparison_scenarios=bulk_insert_100k,update_10k_one_tx,single_insert_5k_tx
```

Each round rotates the three variants: v5/before/after, before/after/v5,
after/v5/before. Both v6 variants run durable default and OS-cache configurations;
their configuration order reverses each round. v5 is measured once per round
and shared by both evaluations. `before/` contains the before raw rounds and a
copy of each shared v5 round (its candidateCommit metadata identifies the before
evaluation; the measured assembly and timings are unchanged). Execution order,
revision identities and selected scenarios are retained in `environment.json`.

Without a scenario subset, both revisions receive the existing vs-v5 verdicts,
with unchanged thresholds, workloads and round counts. The overall verdict and
exit code remain the after revision's gate result. `baselineGate` in `gate.json`
contains the before gate; `comparisons` contains report-only after/before ratios
for every scenario/configuration, including durable writer scaling. The ratios
use median(after)/median(before), with observed min(after)/max(before) and
max(after)/min(before) bounds. These bounds are not confidence intervals.
`gate.csv` distinguishes after/v5, before/v5 and after/before rows; `gate.md`
and the job log/summary include all three tables.

Comparison jobs have a 23-minute measurement budget and 29-minute job timeout.
To keep targeted investigations within that budget, `comparison_scenarios` may
select a comma-separated subset of the ordinary scenario names in `gate.json`.
Subsets require `baseline_ref`: **all results are report-only and no gate verdict
is issued**, even if every selected scenario meets a threshold. Required main
workload setup, dependencies and content checks still execute; unselected main
scenario timings are discarded. Independent small-transaction/checkpoint work
is omitted when unselected, as is durable writer scaling. Warmup uses the same
selection. Round counts and selected workload sizes remain unchanged.
An incomplete run remains an evidence error. Empty `baseline_ref` preserves the
original execution order, reports, gate rules and time budgets.

## Comparable work

Each Linux/Windows/macOS × net8.0/net10.0 job builds both projects from the same
C# sources, against NuGet **[5.0.21]** and the candidate ProjectReference.
Production builds use Release, `TestingEnabled=false`, `GitVersionEnabled=false`.
Default v6 (`DurableCommits=true`) and `dev-os-cache` (`false`) are both measured.
The only conditional API use selects v6's documented durability option and adds
the explicitly separate v6 durable writer scaling scenarios. Turning durability
off does not remove v6's checksum, checkpoint or file-creation sync work.

Each job runs six fresh child processes per configuration, alternating
`v5, dev-default, dev-os-cache` and the reverse. Each child first runs an untimed
2k-document/100-small-transaction workload. Tiered compilation is disabled to
avoid comparing tier-0 and optimized code. Every child owns a unique local-temp
directory and deletes only that directory on exit. Seed: 5021. Data: explicit
integer id, unique secondary integer Key, unindexed integer Value, and a
96-character hexadecimal payload. Collation is ordinal invariant; connection is
Direct, unencrypted, with no initial size. OS caches are not cleared.

Each round creates separate main, small-transaction and checkpoint databases.
Main scenarios run in a fixed order with matching state. Main and small DBs
retain CHECKPOINT=1000. Bulk insert includes BeginTrans, Insert(100k), Commit and
any automatic checkpoint. Point lookups retrieve 10k full documents by indexed
Key using fixed random ids. Full scan materializes 3×100k documents through an
unindexed `Value >= 0` query. Reads follow an untimed full content check. Update
and delete each affect the first 10k documents in one explicit transaction.
Open/close measures 20 sequential writable opens of the checkpointed main DB,
including collection-name enumeration and disposal. Explicit checkpoint uses
100k documents in uncheckpointed WAL (CHECKPOINT=0 on both versions for this
scenario only); it measures page copying/log retirement, not physical WAL shrink.

Rebuild remains excluded equally from both libraries: 5.0.21 threw
**`Detected loop in FindAll({0})`** when recreating the secondary index of the
90k-document replacement, on all six OS/runtime jobs in
[run 40422543627](https://github.com/JKamsker/LiteDB/actions/runs/40422543627).
The loop bound appears to be captured before memory-resident replacement data
reaches disk; that explanation is not instrumented proof of the cause. No
baseline patch or forced rebuild workaround is applied, and no rebuild ratio is
claimed.

The additional durable insert scenarios use 1/4/8 dedicated writer threads on
one Direct database and collection, inserting the same total 5k documents with
one auto-commit transaction per document. Each thread inserts disjoint ids.
Thread creation is untimed; timing includes barrier release, scheduling, inserts
and thread joins. Each thread count gets a fresh database with CHECKPOINT=1000.
They also run at warmup scale before measured work. Results report inserts/s and
median speedup relative to one writer within this job. **v6 scaling only:** v5
has no equivalent per-commit device-sync setting (see its
[EngineSettings](https://github.com/litedb-org/LiteDB/blob/v5.0.21/LiteDB/Engine/EngineSettings.cs)
and [WriteLogDisk](https://github.com/litedb-org/LiteDB/blob/v5.0.21/LiteDB/Engine/Disk/DiskService.cs)).
Its default commit cannot serve as a durable A/B baseline.

## Gate and noise policy

[`gate.json`](gate.json) records the owner-approved Phase 1 limits. For
`dev-os-cache`, median candidate / median 5.0.21 must be ≤1.10 for single-document
auto-commit, bulk insert, scan, update and delete; indexed point lookup must be
≤1.05. Default durability, open/close and checkpoint are report-only; checkpoint
is deferred to Phase 5. Durable multi-thread scaling is also report-only.
There is no combined score across scenarios, hosts or runtimes.

The operational noise cutoff remains `maxSpreadPercent=20`, using
`100 × (max/min − 1)` independently for A and B. A gated scenario fails even
with high spread when `min(B)/max(A) > threshold`. It passes when its median
ratio and `max(B)/min(A)` both meet the threshold. Otherwise, the median verdict
applies only when both spreads are ≤20%; high-spread ambiguous results are
**noisy** and require a rerun. JSON, CSV and Markdown report both observed ratio
bounds; these are not confidence intervals. No rounds are discarded.

Round counts come from `gate.json`: six on Linux/Windows, ten on macOS, with
alternating process order retained. macOS has a 24-minute measurement budget
(including child timeouts) and a 29-minute job limit. Incomplete measurements
remain evidence errors. Thresholds are unchanged.

The evaluator reads raw rounds and independently recomputes medians. Missing or
duplicate rounds/scenarios, invalid timings, wrong operation counts/runtime/OS,
and binary/commit mismatches are evidence errors, not noise. Its exit codes are
0=pass, 1=fail/evidence error, 2=noisy. A stable failure takes precedence in the
job verdict even if other scenarios need reruns. `rerunRequired` records any
noise. When all gated results are pass/noisy, CI emits a warning for noisy and
finishes successfully to request a rerun rather than treating noise as a
performance failure. **That green job is inconclusive; inspect `gate.json` or
the summary.** This advisory check must never be required for merge.

## Evidence and local validation

Artifacts retained for 30 days include per-process JSON/logs (assembly SHA256,
candidate/harness SHA, actual runtime, OS), environment/source hashes, execution
order, raw CSV, summary JSON/CSV with median/min/max/spread/B:A, and gate
JSON/CSV/Markdown with per-OS/runtime/scenario verdicts. Job summaries include
both timings and gate results. Startup, data generation, warmup, content
verification and cleanup are outside scenario timings.

Every stage checks counts, checksums, indexed results and/or contents. Databases
are reopened and verified; the writer scenarios verify every document and
indexed key both before and after reopen. These checks establish workload
correctness under ordinary execution/graceful disposal, not crash or power-loss
safety. The harness changes no library, storage protocol, fault hook, existing
test or coverage filter.

Allowed local validation (builds only, no workload execution):

```sh
dotnet build tools/Ab5021Benchmarks/V5/V5.csproj -c Release -p:TestingEnabled=false -p:GitVersionEnabled=false
dotnet build tools/Ab5021Benchmarks/V6/V6.csproj -c Release -p:TestingEnabled=false -p:GitVersionEnabled=false
python3 -m unittest discover -s .github/scripts -p test_benchmark_gate.py
```

The script tests use synthetic timings; they cover threshold boundaries, stable
regression rejection, median calculations, both sides of noise, report-only
scenarios, incomplete/malformed evidence, identity checks, process ordering,
summaries, and machine-readable CLI output. Run the repository C# size check on
the staged diff as well.
