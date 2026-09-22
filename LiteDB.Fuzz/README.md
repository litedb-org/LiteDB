# LiteDB fuzz runner

`LiteDB.Fuzz` is the deterministic runner for the fuzzing work tracked by
issues #2946 and #2947. It is separate from xUnit so a normal test invocation
stays bounded while the same targets can run for minutes, hours, or days.
The complete review-finding-to-oracle map is in [REVIEW-AUDIT.md](REVIEW-AUDIT.md).

## Quick start

```bash
dotnet build LiteDB.Fuzz/LiteDB.Fuzz.csproj -c Release -p:TestingEnabled=true
dotnet run --project LiteDB.Fuzz -c Release -f net8.0 --no-build -- --list
dotnet run --project LiteDB.Fuzz -c Release -f net8.0 --no-build -- \
  --target query,transaction,page --seed 2947 --count 100 \
  --artifact-dir artifacts_temp/fuzz
```

Every run writes `run.json`, `summary.md`, `trace.jsonl`, `input.bin`,
`input-offsets.jsonl`, and `replay.json`.

Successful duration-bound epochs are compacted after the isolated process exits:
their run metadata, hashes, seed replay, traces, novelty data, and coverage stay,
while redundant `input.bin`, input-offset, and transient database files are removed.
`retention.json` records the applied policy and byte count. Failed, crashed, and
hung runs always retain their exact recorded input and database state.
Novel state signatures and their replayable seeds are retained in
`interesting.jsonl`; the parent runner deduplicates them across isolated target
processes into `interesting-corpus.jsonl`. Existing entries are preserved and
replayed automatically on the next campaign using the same artifact root, so
new semantic coverage feeds future runs rather than being report-only. Retained
entries keep the longest interesting prefix for each target/seed together with
duration mode, exact recorded input prefix, and input/trace hashes. Targets with persistent state also
preserve database/WAL files. Replay a failure
without remembering the original command:

```bash
dotnet run --project LiteDB.Fuzz -c Release -f net8.0 --no-build -- \
  --replay artifacts_temp/fuzz/<run>/replay.json \
  --artifact-dir artifacts_temp/fuzz-replay
```

Common options are `--target`, `--seed`, `--count`, `--duration`, `--workers`,
`--artifact-dir`, `--replay`, and `--coverage-guided`. A duration such as `20m`, `3h`, or `2d`
is a total wall-clock budget shared across the selected target/worker shards; it
does not multiply by the number of processes. Without it, count is the exact case/operation bound.
Workers are deterministic seed shards (`seed + worker * 1,000,003`).
Long shards are split into fresh-process epochs (30 seconds by default, configurable
with `--epoch-duration`) whose seeds include the epoch. This bounds replay time and
varies configuration inside one campaign. Every epoch resets LiteDB's static caches.
Fuzz-owned randomness is persisted as raw little-endian words in `input.bin`;
replay consumes that data instead of regenerating it. Operation offsets make the
stream usable by deletion/mutation reducers and external engines such as SharpFuzz.
Permanent corpus entries pin both input and trace SHA-256 hashes, so generator
changes cannot silently change a regression.
Duration failures store their actual executed prefix and duration-mode metadata,
so replay does not silently fall back to the default count. Invariant failures
also carry stable call-site IDs; prefix minimization accepts only the same ID.
The parent watches a heartbeat updated by `Next()`. Ninety seconds without progress
is recorded as a stable `HANG_*` failure after attempting a process dump; hard exits
also receive parent-written `run.json`, stderr/stdout, and replay metadata.
The original failure artifacts are written before minimization begins. Every
minimization prefix has its own 30-second bound (configurable with
`--minimization-timeout`), and minimization pulses the parent heartbeat without
being allowed to replace the original failure ID.
`--determinism-check` replays recorded bytes and compares trace hashes.

Nightly and campaign runs add `--coverage-guided`. The runner collects actual
LiteDB IL-range execution coverage for each isolated seed, writes the raw XML
beside that run, and retains a seed only when it adds a previously unseen engine
range. `coverage-signatures.txt` and `coverage-corpus.jsonl` persist that feedback;
using the same artifact root automatically replays the retained coverage corpus.

## Targets

| Target | Oracle / invariant |
| --- | --- |
| `query` | independent Boolean tree vs unindexed, indexed, and cached SQL execution |
| `linq-cache` | cached vs direct translation, CLR scalar evaluation, concurrent shared mapper |
| `transaction` | independent multi-collection committed/pending state model |
| `wal` | generated multi-transaction recovery with event-indexed I/O failures and partial writes |
| `page` | slot payload model plus page/footer/accounting/overlap invariants |
| `index` | scalar, multikey, unique, ordering, and key-moving update checks |
| `shared` | real child processes, acknowledged ledgers, and owner-process death |
| `bson` | contiguous vs fragmented reader/writer round trips and mutations |
| `parser` | fresh vs cached SQL/expression parsing, binding, malformed errors |
| `mapper` | supported CLR shape round trips and cyclic failure isolation |
| `storage` | exact byte/metadata model across upload, streaming, delete, rollback |
| `rebuild` | logical snapshots across rebuild, encryption, collation, corruption probes |
| `rebuild-transition` | injected failures at every backup/install transition with complete-state recovery |
| `vector` | deterministic graph churn with live/unique/score/order checks |
| `sort` | reference full sort vs disk sort and Top-N/offset/limit |
| `value` | comparison algebra, JSON, tokenizer, ObjectId, auto-id, connection strings |
| `integrity` | known structural mutations that must be rejected by the raw page-graph oracle |
| `snapshot` | process-isolated cursor snapshots while writers commit and reuse WAL/pages |
| `threaded-snapshot` | barrier-forced same-process writer/checkpoint overlap with multiple live snapshots |
| `concurrent` | one-database multithreaded commits, unique contention, cursors, checkpoint, and rebuild |
| `conflict` | barrier-forced writer/schema/drop/storage/rebuild conflicts with acknowledged-state checks |
| `power-loss` | volatile/durable device model cut at every internal WAL/checkpoint phase |
| `recovery` | dirty-WAL recovery interrupted again by transient and persistent I/O failures |
| `chaos` | combined CRUD/bulk/index/transaction/SQL/storage/rebuild/reopen/auto-checkpoint model |
| `boundary` | exact slots, keys, document/page limits, transaction limits, headers, and nesting |
| `read-only` | byte-identical data/WAL across generated read and read-only workloads |
| `sql-dml` | SQL DML/DDL/transaction/pragma differential against equivalent API state |
| `compatibility` | v4 plain/encrypted upgrade plus dirty WAL, index, storage, and read-only states |
| `api-boundary` | auto IDs, typed storage IDs, names, JSON, pragmas, system collections, and dates |
| `storage-failure` | throwing user streams, typed IDs, open-reader overwrite, and random seeks |
| `pressure` | observed cache eviction under tiny auto-checkpoints and pinned readers |
| `malformed-file` | grammar-aware header/page/WAL corruption and truncation contracts |
| `oracle-selftest` | controlled bad states that every core invariant family must reject |

Persistent targets checkpoint and invoke an independent raw-file walker. It
checks page accounting and ownership, empty/data/index free lists, data chains,
skip-list reachability/order/uniqueness, vector graph links, primary keys, and
secondary keys independently re-evaluated from each document. The same oracle
handles preallocated and encrypted files. Its mutation suite covers structural,
semantic-index, data-chain, ownership, and vector-link corruption, and its
checked-in legacy corpus exercises v8 databases created before the current
engine. Targets also record interesting-path counters and enforce minimum
coverage once a run is large enough for the threshold to be meaningful.

Important internal events are proved rather than inferred. Crash markers are
durably written by the crashing child, and test-only observers count actual
sort spills and cache evictions. Those observers and all failure-injection
hooks are compiled only for `DEBUG` or `TestingEnabled=true`; production builds
contain neither the fields nor the calls. `oracle-selftest` mutation-tests the
acknowledged-state, crash-marker, cache A-B-A, uniqueness, secondary-order,
snapshot, and vector-score invariant families with deliberately bad results.

The shared-process target kills children at API boundaries and at test-only
internal WAL/checkpoint gates: page and confirmation writes, durable flush,
transaction confirmation, checkpoint page copies, data flush, and WAL clear.
The complementary power-loss target separates volatile bytes from durable bytes:
ordinary `Flush()` leaves data volatile, the engine's durable flush promotes it,
and each modeled power cut discards the remaining volatile state before recovery.

Ordinary v8 compatibility has a separate process-level differential campaign.
Both current dev and LiteDB 5.0.21 create plain and encrypted files, the other
engine mutates them, and the creator reopens them and compares logical and
secondary-index snapshots:

```bash
python3 scripts/test-v8-differential.py --seeds 3 --operations 80
```

## Execution tiers

The `Fuzz` workflow shards PR smoke targets across Linux x64/Arm64, Windows,
and macOS, runs both .NET 8 and .NET 10, and varies timezone/culture without duplicating the full seed range.
Windows also runs persistence/crash targets. A small x86/x64 .NET Framework 4.8.1
harness executes the `netstandard2.0` BSON/index/sort paths under NLS. Scheduled
nightly jobs use a `github.run_id`-derived seed and a 150-minute total budget under
execution-coverage collection, bounded by the three-hour shard timeout; the v5
differential also runs nightly. Failures are grouped by stable ID, summarized, and
open or update one upstream issue while retaining raw artifacts. A manual `campaign` dispatch
accepts hour/day durations and requires a self-hosted runner labeled
`self-hosted`, `linux`, `x64`, and `litedb-fuzz`; it is intentionally never
scheduled because GitHub-hosted jobs cannot complete 24–72 hour campaigns.

Suggested local tiers:

```bash
# PR-like smoke
dotnet run --project LiteDB.Fuzz -c Release -f net8.0 -- --target all --seed 2947 --count 30

# focused investigation
dotnet run --project LiteDB.Fuzz -c Release -f net8.0 -- --target wal,shared --seed 50000 --duration 2h --workers 2

# opt-in long campaign
dotnet run --project LiteDB.Fuzz -c Release -f net8.0 -- --target all --seed 100000 --duration 48h --workers 4
```

The permanent seed corpus is in `Corpus/regressions.json` and is executed once
for each selected target in every normal runner invocation, in addition to the
requested generated seed shards. A new real finding
should be minimized, added there with its target and reason, and accompanied by
a focused xUnit regression whenever practical.

## Publishing raw results

GitHub Actions always uploads the raw run directories. Maintainers can also
publish a completed local/hosted campaign to `litedb-org/LiteDB-Artifacts`:

```bash
scripts/publish-fuzz-artifacts.sh artifacts_temp/fuzz issue-2947-smoke
```

The publisher refuses dirty artifact clones, creates a date/label directory,
commits the unchanged raw files plus summaries, and pushes `main`.
