# Fuzzing review audit

This maps every finding in PR review comment `5757585635` to an executable
target and its required evidence. A target fails when the evidence is absent;
the entries are not coverage claims inferred from configuration.

| # | Finding | Executable coverage and proof |
|---:|---|---|
| 1 | Acknowledged power-loss state | `power-loss` tracks the return of each operation and compares complete serialized documents and FileStorage bytes. Only the currently interrupted operation may resolve to old or new state. |
| 2 | Shared crash false positives | `shared` writes a unique durable intent marker at the reached hook; the parent requires the matching marker and exit mode and records observed, not requested, boundaries. |
| 3 | Live snapshot overlap | `threaded-snapshot` holds N and N+1 readers while two writers demonstrably complete, then overlaps checkpoint/reuse and checks all three versions. |
| 4 | Malformed public contracts | `bson`, `parser`, `api-boundary`, and `malformed-file` separate known-invalid inputs, whitelist public failures, verify rejection, and prove subsequent usability/pristine state. |
| 5 | Vector combinations/oracle | `vector` independently selects all 18 dimension/metric pairs, computes reference distances without production code, brute-forces live rows, and enforces ANN recall and invalid-vector behavior. |
| 6 | Unique conflicts | `index` covers exact and collation-equivalent inserts, moving updates, mid-bulk conflicts, failed index creation, transaction rollback, reopen, and raw integrity. `concurrent` adds same-key contention. |
| 7 | Mapper tree oracle | `mapper` compares the first serialization to an independently constructed BSON tree, including attributes, custom types, constructors, interfaces, null/defaults, dates, and cycles. |
| 8 | Combined chaos | `chaos` mixes CRUD, bulk mutations, unique/scalar indexes, FileStorage, transactions, rename/drop, pragmas, tiny auto-checkpoints, reopen, live readers, rebuild collation/password changes, SQL, and throwing streams against full models. Persistence failure transitions are shared with `wal`, `power-loss`, and `recovery`. |
| 9 | Same-process concurrency | `concurrent`, `threaded-snapshot`, and `conflict` use barriers/long-running tasks on one `LiteDatabase` for same-row writes, unique contention, cursors, checkpoints, schema changes, collection drop, FileStorage overwrite, and rebuild. |
| 10 | SQL state machine | `sql-dml` differentially executes INSERT, generated UPDATE/DELETE, CREATE/DROP INDEX, DROP/RENAME COLLECTION, BEGIN/COMMIT/ROLLBACK, PRAGMA, CHECKPOINT, and REBUILD. |
| 11 | Generated bulk mutations | `sql-dml`, `chaos`, and `index` generate OR/range/IN/multikey predicates, key/cardinality-changing transforms, unique conflicts, small transaction limits, exact counts, and full-state comparisons. |
| 12 | Generated crash workload | `power-loss` varies occurrence, payload/page size, insert/update/delete/index/FileStorage operations, encryption, transactions, and checkpoint cuts. `wal` adds event-indexed partial writes and larger generated schedules. |
| 13 | Failure during recovery | `recovery` interrupts dirty-WAL recovery again with transient and persistent read/write failures, reopens, compares all acknowledged documents, and runs raw integrity. |
| 14 | Conflicting operations | `conflict` forces same-document/schema/drop/same-file/rebuild races and records an acknowledged-state/event proof; `concurrent` forces unique-key winners. |
| 15 | Deliberate cache reuse | `parser` exercises typed/null A-B-A, interleaved readers, schema churn, errors, aggregates, projections, INCLUDE, GROUP/HAVING, Count/LongCount/Exists, First/Single, Into, and ForUpdate; `linq-cache` compares cached/direct translation concurrently. |
| 16 | User-code failures | `mapper` injects getter, constructor, custom serializer, cycle, and mid-enumeration failures. `storage-failure`, `chaos`, and `storage` inject source/destination stream failures and require rollback/usability. |
| 17 | Rebuild/storage transitions | `rebuild-transition` faults all backup/install gates while rebuilding indexes/FileStorage and changing passwords. `storage-failure` checks seek bytes, position, open-reader overwrite, and failed streams. |
| 18 | Auto-checkpoint | `pressure` and `chaos` use checkpoint sizes 1-3 under write pressure; `power-loss` cuts the internal checkpoint gates. |
| 19 | Read-only dirty WAL | `read-only` starts with committed uncheckpointed WAL, hashes data/WAL before and after, attempts every mutation family, and proves continued reads. |
| 20 | Legacy compatibility | `compatibility` upgrades plain/encrypted v4/v7 fixtures, interrupts and resumes every publication phase, then adds indexes, FileStorage, dirty WAL, and read-only opens. The process-level v8/5.0.21 differential remains separate. |
| 21 | Cache/resource pressure | `pressure` randomizes the constrained profile/limits, holds readers, churns multipage values, and requires an observed test-build eviction event. |
| 22 | Sort cleanup/scale | `sort` requires a real observed spill, long string/binary keys, ties, Top-N/full-sort parity, repeated early disposal with bounded temp bytes, and injected temp-stream exhaustion with caller-stream ownership preserved. |
| 23 | Datafile/WAL mutations | `malformed-file` mutates/truncates page headers and dirty WAL confirmation/transaction/page structure, duplicates pages, and damages encrypted preambles. `integrity` adds semantic graph mutants. |
| 24 | Pragmas/dates | `api-boundary` and `sql-dml` cover UserVersion, Timeout, LimitSize, UtcDate, CheckpointSize, invalid values, persistence, SQL equivalents, UTC/ambiguous/invalid local dates, and the timezone matrix. |
| 25 | API/format boundaries | `api-boundary` covers all auto-ID modes with rollback/reopen, typed storage IDs, mapper customization, strict JSON, system collections, Unicode/case/reserved/max names, and header/name limits (with `boundary`). |

## Campaign feedback and oracle validation

`--coverage-guided` runs each isolated seed under `dotnet-coverage`, records raw
LiteDB IL-range XML, retains only seeds that add new engine execution ranges,
and replays `coverage-corpus.jsonl` in later campaigns. Nightly and campaign
workflows cache both execution and semantic corpora between runs. Important
events (crash, spill, eviction, overlap, uniqueness, conflict) have explicit
counters/markers and minimum checks.

`oracle-selftest` feeds controlled bad acknowledged states, crash markers,
cache A-B-A results, uniqueness sets, secondary ordering, snapshots, and vector
scores to the same invariant families and requires every mutation to be killed.
All library observers and fault hooks are compiled only under `DEBUG` or
`TestingEnabled=true`; a production Release assembly contains neither their
members, calls, nor phase strings.

## Runner and CI follow-up audit

This also closes every finding in follow-up comment `5757925582`:

| # | Runner/CI finding | Executable coverage and proof |
|---:|---|---|
| 1 | Multiplied duration | `--duration` is one total wall-clock budget. It is divided by run count and the four-slot process pool; the campaign cannot request more work than its job timeout permits. |
| 2 | Constant nightly seeds | Nightly derives and logs each shard seed from `github.run_id`; fixed seeds remain only in PR smoke and the pinned regression corpus. |
| 3 | No hang watchdog | Children update a one-second heartbeat. The parent records `HANG_<target>`, attempts a `dotnet-dump`, kills the process tree, and preserves replay/stdout/stderr after 90 seconds without an operation boundary. |
| 4 | Hard crashes lose artifacts | The parent writes fallback `run.json`, `replay.json`, summary, exit code, stdout, and stderr when a child exits before writing its own result. |
| 5 | Ever-growing scenarios | Duration shards use fresh processes and derived seeds in bounded epochs (30 seconds locally, two minutes in long coverage CI), so a failure has a short standalone input. |
| 6 | RNG streams rather than data | Each generated word is persisted in `input.bin`; replay consumes that file and `input-offsets.jsonl` marks operation boundaries for reducers. Failure/crash/hang inputs are always retained. Successful duration epochs retain hashes and seed replay while pruning redundant input/state after exit; the Linux PR shard executes and asserts that retention contract. Corpus hashes reject generator drift. |

The additional gaps have dedicated coverage: `query` now checks `p AND true`,
predicate/complement partitions, descending LIMIT/OFFSET, mutations between
queries, multikey indexes, and expression indexes. A PR determinism shard replays
recorded input and compares traces. Testing builds poison released page buffers
with `0xDD`. Windows runs WAL/shared/power-loss/recovery, while x86 and x64
.NET Framework 4.8.1 run the netstandard serializer/index/sort paths under NLS.
The v5 differential runs nightly, linked generator paths trigger the workflow,
workflow-dispatch inputs pass through environment variables, and nightly failures
are grouped by stable ID before one issue is opened or updated.
