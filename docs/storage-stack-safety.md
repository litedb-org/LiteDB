# Storage stack safety acceptance map

This maps the persistent invariants of the v10 checksum, v11 index-ordering,
v12 compact-storage and v13 WAL-retirement layers to executable checks. It is an
acceptance map, not a record of passing runs. Merge evidence must identify the
tested revision, configuration, platform and results; a parent-layer result does
not establish that a dependent layer preserves the same invariant. The
[data-safety gate](rules/data-safety.md) and
[validation rules](rules/validation.md) apply to the complete integrated stack.

## What the oracles establish

Recovery checks must compare complete expected documents, including nested values,
types and payload bytes, rather than just record counts or a generation field.
Secondary-index checks compare expected keys and result sets, including absent old
keys. Where a particular plan matters, tests assert index selection; comparing two
queries that both scan documents would not validate a persisted index.

Untouched collections and unrelated-file sentinels detect collateral changes.
Read-only tests compare both data and WAL bytes before and after inspection,
including rejection paths. Writable recovery is followed by another open, and
completed migrations/checkpoints are retried to check repeatability. These are
separate checks: a successful open, valid page CRC, or unchanged document count is
not a substitute for logical consistency or byte preservation.

Fuzz targets retain generated decisions and input/trace hashes for replay. Their
models, raw integrity checks and failure-artifact policy are described in the
[fuzz runner](../LiteDB.Fuzz/README.md) and
[oracle audit](../LiteDB.Fuzz/REVIEW-AUDIT.md). Not every target uses every oracle;
the focused suites below define the relevant assertions.

## v10: checksums, WAL proofs and header publication

Design: [page and WAL checksums](page-and-wal-checksums.md),
[header publication and recovery](header-publication.md).

| Invariant | Discriminating evidence |
| --- | --- |
| A valid frame CRC does not make an incomplete transaction committed. Frame identity, generation, count/digest and confirmation sequence must agree. | [WalChecksum_Tests](../LiteDB.Tests/Internals/WalChecksum_Tests.cs), [WalPowerLoss_Tests](../LiteDB.Tests/Internals/WalPowerLoss_Tests.cs), [WalTransactionBoundary_Tests](../LiteDB.Tests/Internals/WalTransactionBoundary_Tests.cs); `checksum-wal`, `wal`, `power-loss`. |
| No commit is written behind a torn frame. A failed write that may leave one (an overwrite of a slot, an append whose truncation failed or had to keep a header journal, a checkpoint's own WAL writes) stops the engine before the WAL writer is released, whatever the exception type; a failed append that was truncated releases its position, so the next append rewrites the slot. A caller stream other than a `MemoryStream` may hold frames after their write returned (a `BufferedStream`): a failure while it may still hold a frame of the batch stops the engine until the batch's final flush succeeded, and a reader's access first flushes what it holds for the writer and hands a failure there to the writer, whose batch fails. Such a stream is flushed once per batch and per sync, not per page. A transaction whose safepoint write failed only rolls back (reads included), and a format promotion that fails with its header journal outstanding stops the engine. The failure is recorded before the stop, and the engine's next call reopens it read-only from the files as they are ([decisions](decisions/durability-policy.md) 6): it writes and syncs nothing more through the handles that failed, reads keep working, every later write throws with the record, and a cold reopen recovers exactly the acknowledged commits. A transaction's own WAL slot that a `$dump`/`$page_list` snapshot still pins is not rewritten: the new version is appended. | [TornSlotRewrite_Tests](../LiteDB.Tests/Regressions/TornSlotRewrite_Tests.cs) (a commit forced into the window after the writer's release); [TornWalAppend_Tests](../LiteDB.Tests/Regressions/TornWalAppend_Tests.cs) (half-written frames, complete frames whose write reported failure, a BufferedStream and a stream that tears a held frame at the next write or the flush); [SharedBufferedLogStream_Tests](../LiteDB.Tests/Regressions/SharedBufferedLogStream_Tests.cs) (a reader in the window after a frame write, with an I/O, a non-I/O and no failure); [CallerStreamFlushCost_Tests](../LiteDB.Tests/Regressions/CallerStreamFlushCost_Tests.cs) (flushes per commit and checkpoint, not per page); [CheckpointFailureWindow_Tests](../LiteDB.Tests/Regressions/CheckpointFailureWindow_Tests.cs) (failed retirement records; the journal guard behind the stop; the engine is closed); [FailedSafepointWrite_Tests](../LiteDB.Tests/Regressions/FailedSafepointWrite_Tests.cs); [FailedPromotionJournal_Tests](../LiteDB.Tests/Regressions/FailedPromotionJournal_Tests.cs); [DurabilityPolicy_Tests](../LiteDB.Tests/Regressions/DurabilityPolicy_Tests.cs) (another thread's explicit transaction after the failure); [DumpPinnedWalSlot_Tests](../LiteDB.Tests/Regressions/DumpPinnedWalSlot_Tests.cs). |
| Legacy checksum cutover is bounded and repeatable; mixed page coverage cannot become permission to ignore damaged checked pages. | [LazyChecksumCutover_Tests](../LiteDB.Tests/Internals/LazyChecksumCutover_Tests.cs), [LazyChecksumMigration_Tests](../LiteDB.Tests/Internals/LazyChecksumMigration_Tests.cs), [LazyChecksumMarker_Tests](../LiteDB.Tests/Internals/LazyChecksumMarker_Tests.cs); `checksum-page`, `checksum-migration`. |
| An in-place checkpoint, format promotion, conversion or invalid-state header marker requires durable WAL and journal bytes, in both commit modes (decision D of the [durability decisions](decisions/durability-policy.md)). A failed sync must stop before data overwrites, preventing mixed old/new documents and stale indexes. A log that answers "cannot sync" (#2242) refuses the overwrite before the journal is written: without durable commits a checkpoint writes nothing and keeps the WAL, a compact write stores BSON, a conversion or promotion opens read-only with both files unchanged, and none of these is a failure; with durable commits the barrier throws "The log file cannot sync" (an explicit checkpoint throws it, an open that must convert or promote opens read-only with it as its reason). A checkpoint writes only to a data file that just synced. Storage that cannot sync never retires or reuses WAL frames; a fresh engine proves the data file and the log once before it first reuses a slot. | [CheckpointDurability_Tests](../LiteDB.Tests/Internals/CheckpointDurability_Tests.cs) rejects the padding and journal barriers on an I/O error, checks zero data writes, the recorded failure and stopped subsequent writes, and recovers both cached and durable images; [OverwriteBarrier_Tests](../LiteDB.Tests/Regressions/OverwriteBarrier_Tests.cs) (kept WAL, byte-identical data file, power-loss images); [Issue2242_UnsyncableLog_Tests](../LiteDB.Tests/Issues/Issue2242_UnsyncableLog_Tests.cs), [IndexPromotionDurability_Tests](../LiteDB.Tests/Engine/IndexPromotionDurability_Tests.cs), [MvccUnsyncableLog_Tests](../LiteDB.Tests/Internals/MvccUnsyncableLog_Tests.cs), [SharedUnsyncableLog_Tests](../LiteDB.Tests/Internals/SharedUnsyncableLog_Tests.cs), [UnsyncableRetirement_Tests](../LiteDB.Tests/Regressions/UnsyncableRetirement_Tests.cs), [RetiredSlotPowerLoss_Tests](../LiteDB.Tests/Regressions/RetiredSlotPowerLoss_Tests.cs) and [SlotReuseWithoutProof_Tests](../LiteDB.Tests/Regressions/SlotReuseWithoutProof_Tests.cs) cover conversion, promotion, checkpoint, retirement and slot reuse on storage that cannot sync. |
| The log is emptied or shrunk, a header journal or legacy header backup retired, and retired WAL slots cleared only behind a data sync that covered every data write; a data file that cannot sync keeps the WAL (commits stay durable there, `$database.walKept`), and a data file that stops syncing after an operation wrote stops it before it removes anything. Before a header journal is retired, recovery writes the header back as read (a failed sync may have left it in the page cache only). | [DataSyncConcurrency_Tests](../LiteDB.Tests/Regressions/DataSyncConcurrency_Tests.cs), [KeptWalStop_Tests](../LiteDB.Tests/Regressions/KeptWalStop_Tests.cs), [DataFileStopsSyncing_Tests](../LiteDB.Tests/Regressions/DataFileStopsSyncing_Tests.cs), [UnsyncableDataFile_Tests](../LiteDB.Tests/Regressions/UnsyncableDataFile_Tests.cs), [UnsyncedPromotion_Tests](../LiteDB.Tests/Regressions/UnsyncedPromotion_Tests.cs), [KeptJournalPromotion_Tests](../LiteDB.Tests/Regressions/KeptJournalPromotion_Tests.cs), [CheckpointDataSync_Tests](../LiteDB.Tests/Regressions/CheckpointDataSync_Tests.cs), [SyncFailureRecovery_Tests](../LiteDB.Tests/Regressions/SyncFailureRecovery_Tests.cs). |
| A durable commit is never acknowledged where it is not durable ([decisions](decisions/durability-policy.md) 3, 9): before an engine's first WAL batch the log file and its directory are proven to sync, once per log path per process, and a log or directory that answers "cannot sync" fails the commit before it writes a frame; a log that stops syncing after the proof fails the commit whose sync found out. "Cannot sync" and a failed sync stay distinct; opted-out commits degrade as before. `Exception.Data["LiteDB.CommitOutcome"]` is `NotCommitted` only for a provable abort (refused before its first frame, or its confirmation's failed append truncated away and that truncation synced) and `Unknown` otherwise (note 14). The WAL kept while the data (or, opted out, the log) cannot sync grows only up to `wal limit` (default 1 GiB): past it a write that starts throws, after retrying the syncs through the #3052 helpers, and reads keep working. A recorded failure stops the engine, whose next call reopens it read-only: reads keep working, every write throws with the record, and `$database` reports `readOnly`, `readOnlyReason`, `writeFailure`, `walKept`, `walLimit` and 64-bit sizes; an open that must convert, migrate or repair where the storage cannot sync opens read-only instead of failing. | [DurabilityPolicy_Tests](../LiteDB.Tests/Regressions/DurabilityPolicy_Tests.cs), [NativeFileSync_Tests](../LiteDB.Tests/Issues/NativeFileSync_Tests.cs), [WalDirectorySync_Tests](../LiteDB.Tests/Issues/WalDirectorySync_Tests.cs), [UnreadableWalDirectory_Tests](../LiteDB.Tests/Regressions/UnreadableWalDirectory_Tests.cs), [Issue2818_FlushFallback_Tests](../LiteDB.Tests/Issues/Issue2818_FlushFallback_Tests.cs), [CheckpointDurability_Tests](../LiteDB.Tests/Internals/CheckpointDurability_Tests.cs) (fail-loud admission and unknown outcome); [WrittenConfirmation_Tests](../LiteDB.Tests/Regressions/WrittenConfirmation_Tests.cs) and [AcknowledgedReopen_Tests](../LiteDB.Tests/Regressions/AcknowledgedReopen_Tests.cs) (the commit outcome, with failed and unreportable cleanup syncs and non-I/O exceptions); [WalLimit_Tests](../LiteDB.Tests/Regressions/WalLimit_Tests.cs), [FreshEngineLogBarrier_Tests](../LiteDB.Tests/Regressions/FreshEngineLogBarrier_Tests.cs) (the limit); [UnsyncedReadOnlyOpen_Tests](../LiteDB.Tests/Regressions/UnsyncedReadOnlyOpen_Tests.cs), [RecordedFailure_Tests](../LiteDB.Tests/Regressions/RecordedFailure_Tests.cs), [TornWalAppend_Tests](../LiteDB.Tests/Regressions/TornWalAppend_Tests.cs) (read-only continuation and the open-time fallback). |
| A sync helper that runs after an earlier admission check (`$database.walKept`'s log and data syncs, a checkpoint's syncs, a promotion) rechecks the stopped and recorded-failure state once it holds the lock that orders its sync, and records a real I/O failure of its own sync before it releases that lock, so a waiting writer, checkpoint or helper syncs and writes nothing more (#3052). "Cannot sync" is never recorded this way. | [SyncFailureBoundary_Tests](../LiteDB.Tests/Regressions/SyncFailureBoundary_Tests.cs) forces both interleavings for the log and data syncs and a waiting checkpoint, with a "cannot sync" control; [CheckpointFailureWindow_Tests](../LiteDB.Tests/Regressions/CheckpointFailureWindow_Tests.cs) (a promotion waiting on a failed checkpoint syncs and writes nothing). |
| Torn header publication and interrupted repair retain usable recovery evidence. A second interruption must not destroy an unsealed intent or the selected repair journal. | [ChecksumCheckpointCrash_Tests](../LiteDB.Tests/Internals/ChecksumCheckpointCrash_Tests.cs), [HeaderJournalFailure_Tests](../LiteDB.Tests/Internals/HeaderJournalFailure_Tests.cs), [ConversionJournalCrash_Tests](../LiteDB.Tests/Internals/ConversionJournalCrash_Tests.cs), [ConversionIntentRecovery_Tests](../LiteDB.Tests/Internals/ConversionIntentRecovery_Tests.cs); `checksum-crash`, `recovery`. |
| Interrupted encrypted WAL creation may complete only a validated empty preamble. Salt/check-prefix bytes survive repeated interruption; wrong passwords, foreign bytes and existing payloads must not be reinitialized. | [EncryptedWalCreation_Tests](../LiteDB.Tests/Internals/EncryptedWalCreation_Tests.cs) exercises physical write cuts, repeated completion, real files, read-only/rebuild inspection and rejection preservation. [EncryptedWalCreationFailure_Tests](../LiteDB.Tests/Internals/EncryptedWalCreationFailure_Tests.cs) rejects each preamble sync with I/O and unsupported-sync errors. |

## v11: persisted index ordering

Design: [index migration and runtime compatibility](collation-runtime-compatibility.md).

| Invariant | Discriminating evidence |
| --- | --- |
| Migration preserves documents and regenerates affected expression/multikey results under the current comparer. Unique collisions, invalid keys and insufficient capacity reject before publication. | [IndexMigration_Tests](../LiteDB.Tests/Engine/IndexMigration_Tests.cs), [IndexMigrationPreflight_Tests](../LiteDB.Tests/Engine/IndexMigrationPreflight_Tests.cs), [IndexMigrationCapacity_Tests](../LiteDB.Tests/Engine/IndexMigrationCapacity_Tests.cs), [IndexMigrationVectorCapacity_Tests](../LiteDB.Tests/Engine/IndexMigrationVectorCapacity_Tests.cs); `index` and the separate-process compatibility probes below. |
| The version barrier precedes migration WAL, and all indexes plus the completed ordering revision commit together. Retrying a migration after another power loss preserves the original logical database. | [IndexMigrationPowerLoss_Tests](../LiteDB.Tests/Engine/IndexMigrationPowerLoss_Tests.cs) uses the checked-in [LiteDB 5.0.21 fixture](../LiteDB.Tests/Resources/IndexMigration_5_0_21.zip), not a current file with only its version byte changed. It captures migration/checkpoint fault images and interrupts recovery again, with plaintext/encryption, complete payloads, independently ordered ObjectIds, computed/multikey index plans and an untouched collection. |
| Process termination and I/O failure during real-file migration remain recoverable. Pending migration is not silently treated as read-only success. | [IndexMigrationRecovery_Tests](../LiteDB.Tests/Engine/IndexMigrationRecovery_Tests.cs), [IndexMigrationPowerLoss_Tests](../LiteDB.Tests/Engine/IndexMigrationPowerLoss_Tests.cs), [test-index-migration-recovery.py](../scripts/test-index-migration-recovery.py). |

Migration may rewrite indexes and require temporary sort/WAL capacity; it does not
require rewriting all documents. Read-only access can reject a pending migration
without changing its sources. Locale-sensitive ordering still requires the
runtime/collation coverage described in the design document.

## v12: compact documents and schema persistence

Design: [compact document storage](compact-document-storage.md).

| Invariant | Discriminating evidence |
| --- | --- |
| v12 publication precedes schema/compact WAL bytes. Interrupted promotion and repeated repair preserve complete BSON/compact contents and indexes. | [CompactPromotionPowerLoss_Tests](../LiteDB.Tests/Engine/CompactPromotionPowerLoss_Tests.cs), [CompactPromotionRecovery_Tests](../LiteDB.Tests/Engine/CompactPromotionRecovery_Tests.cs), [CompactPromotionFileRecovery_Tests](../LiteDB.Tests/Engine/CompactPromotionFileRecovery_Tests.cs); `compact-crash`, `compact-power-loss`. |
| Schema and document changes are transactional through catalog rollover, drop/reuse, rollback, safepoints and reopen; mixed representations remain readable. | [CompactSchemaRecovery_Tests](../LiteDB.Tests/Engine/CompactSchemaRecovery_Tests.cs), [CompactStorageFault_Tests](../LiteDB.Tests/Engine/CompactStorageFault_Tests.cs), [CompactStorageSnapshot_Tests](../LiteDB.Tests/Engine/CompactStorageSnapshot_Tests.cs), [CompactBsonRebuild_Tests](../LiteDB.Tests/Engine/CompactBsonRebuild_Tests.cs); `compact-storage`. |
| Compact encoding preserves BSON admission limits and cannot truncate a vector dimension into its UInt16 field. A rejected oversized vector cannot publish earlier documents or schemas from the failed transaction. | [CompactVectorBounds_Tests](../LiteDB.Tests/Engine/CompactVectorBounds_Tests.cs) checks oversized and maximum-length vectors at root/nested/array positions, encrypted/plain real-file reopen, complete committed payloads and secondary-index results. [CompactStorageAdmission_Tests](../LiteDB.Tests/Engine/CompactStorageAdmission_Tests.cs), [CompactCodec_Tests](../LiteDB.Tests/Engine/CompactCodec_Tests.cs), and `compact-codec` cover additional size/layout admission. |

Changing write mode does not convert existing documents. Explicit BSON rebuild
retains current checksum/index semantics; it does not restore compatibility with
released v8/v9 engines.

## v13: snapshot checkpointing and retirement

Design: [snapshot checkpointing](mvcc-checkpoint.md),
[retirement witnesses and header root](mvcc-retirement-format.md).

| Invariant | Discriminating evidence |
| --- | --- |
| Checkpoint must share the current transaction-header monitor after WAL recovery or legacy checksum migration replaces the header object. A durable commit cannot be validated before its index publication completes. | [MvccRecoveredCommitLock_Tests](../LiteDB.Tests/Internals/MvccRecoveredCommitLock_Tests.cs) pauses after durable WAL write and before index confirmation, then requires checkpoint to wait. It covers recovered/legacy/checkpointed states, ordinary/header-changing commits, encryption and storage modes. |
| Every live snapshot retains its page floors, including unvisited pages and offsets already resolved by another process. Unknown lease state cannot permit reclamation. | [MvccCheckpoint_Tests](../LiteDB.Tests/Internals/MvccCheckpoint_Tests.cs), [MvccReclamationRace_Tests](../LiteDB.Tests/Internals/MvccReclamationRace_Tests.cs), [MvccSharedRegistry_Tests](../LiteDB.Tests/Internals/MvccSharedRegistry_Tests.cs), [MvccSharedProcess_Tests](../LiteDB.Tests/Internals/MvccSharedProcess_Tests.cs), [MvccReclamationProcess_Tests](../LiteDB.Tests/Internals/MvccReclamationProcess_Tests.cs); `threaded-snapshot`, `concurrent`, `transaction-gate`, `cursor-handoff`. |
| Shared mode keeps pre-v13 costs and cleanup: writes while the same thread iterates a leased reader reuse one engine, the last leased reader's disposal checkpoints away the WAL, and a reader that cannot register a lease streams under the mutex. The pin's mutex is owned by a holder thread, never by the iterating thread, so disposing a reader or the engine on another thread, the owner thread exiting, or the owner idling ends it; other threads and processes are never blocked by an idle pin. No mutex recursion leaks, including when an explicit transaction runs inside the iteration. | [SharedReaderWrites_Tests](../LiteDB.Tests/Engine/SharedReaderWrites_Tests.cs) counts engine opens and checks WAL removal plus a data-file-only copy, plain and encrypted; [SharedReaderPin_Tests](../LiteDB.Tests/Engine/SharedReaderPin_Tests.cs) ends pins from other threads, after an await, by owner exit and by the idle limit, including for another process; [SharedReaderModel_Tests](../LiteDB.Tests/Engine/SharedReaderModel_Tests.cs) and [MvccCursorModel_Tests](../LiteDB.Tests/Internals/MvccCursorModel_Tests.cs) compare held readers across partial checkpoints with an independent model. |
| A durable root and witness chain authorize retirement without weakening complete-transaction proofs. Malformed metadata, unwitnessed damage and missing required commits fail before destructive tail cleanup. | [MvccRetirementCrash_Tests](../LiteDB.Tests/Internals/MvccRetirementCrash_Tests.cs), [MvccRetirementCorruption_Tests](../LiteDB.Tests/Internals/MvccRetirementCorruption_Tests.cs), [MvccSafepointRetirement_Tests](../LiteDB.Tests/Internals/MvccSafepointRetirement_Tests.cs); `mvcc-retirement`. Retain the v10 missing-frame/count/digest/sequence-gap tests as well. |
| Full checkpoint from an already published/reused retirement generation clears the root and rotates salt only after durable data. Lost truncation, torn data/header writes, failed syncs and two torn repairs remain recoverable. | [MvccRootedCheckpoint_Tests](../LiteDB.Tests/Internals/MvccRootedCheckpoint_Tests.cs), shared [scenario/oracle](../LiteDB.Tests/Internals/MvccRootedCheckpointScenario.cs), and `mvcc-checkpoint` start with an asserted nonzero root, compare full payloads/indexes/untouched data, verify read-only byte preservation, and check root/WAL removal and salt rotation on retry. |

## Separate-process format boundaries

Rejection must preserve both the data file and WAL, including their existence and
length where checked. Checking only an exception can miss an older reader trimming
the WAL before rejecting the header. Use real predecessor assemblies; a current
binary configured to reject a version does not exercise predecessor startup I/O.

| Probe | Boundary exercised |
| --- | --- |
| [test-vector-compatibility.py](../scripts/test-vector-compatibility.py) | Released LiteDB 5.0.21, legacy conversion/vector boundaries and newer-format rejection. |
| [test-v9-compatibility.py](../scripts/test-v9-compatibility.py) | Actual v9 predecessor creates vector/scalar indexes and clean/dirty WAL; current migrates through checksums/index ordering, compact writes and rooted v13 retirement. Full payloads, vector self-neighbors, scalar plans/results, reopen and v9 rejection preserve the bridge across all layers, plain/encrypted. |
| [test-index-compatibility.py](../scripts/test-index-compatibility.py) | Released 5.0.21 creates genuine old indexes; current migrates/verifies them and the released reader refuses the promoted format. |
| [test-compact-compatibility.py](../scripts/test-compact-compatibility.py) | Released legacy fixtures, mixed/array-only compact promotion, BSON rebuild and released-reader rejection. |
| [test-parent-format-compatibility.py](../scripts/test-parent-format-compatibility.py) | Pinned actual v10 and v11 predecessors reject v11 and v12 fixtures without changing data or WAL. |
| [test-mvcc-compatibility.py](../scripts/test-mvcc-compatibility.py) | Pinned actual v12 predecessor rejects v13 retained-WAL fixtures without changing either file. |
| [test-v8-differential.py](../scripts/test-v8-differential.py) | Separate released/current processes compare generated mutations and complete logical/index results across plain/encrypted legacy and upgraded files. |

The parent-ref arguments are intentional compatibility boundaries. Updating a pin
requires checking that the selected binary still lacks the newer capability.

## Fault model and operational limits

The crash devices distinguish volatile bytes from durable bytes. Tests include
lost unsynced writes, prefix/sector tears, selected later writes persisting first,
failed reads/writes/syncs, process termination, corruption and repeated recovery.
The exact fault combinations differ by suite; finite tests and fuzz campaigns do
not establish safety for every possible schedule or hardware failure.

Successful durable sync must persist its addressed bytes. Power-safe overwrite
must preserve previously synced bytes outside the write range, including neighboring
bytes in a physical sector shared by WAL frames. CRCs detect accidental damage,
not adversarial rewriting. Independent damage to required data and recovery copies
can be unrecoverable. Ordinary commit fallback reports reduced durability.
Checkpoint, publication and preamble barriers attempt an actual sync; a failed
sync stops them, is recorded, and the engine continues read-only until the database is
reopened ([decisions](decisions/durability-policy.md) 6; `$database.writeFailure`). A log that
answers "cannot sync" (#2242) is such a failure with durable commits (the default): a commit
throws before it writes a frame, a barrier before the data file is written. Only with
`durable commits=false` does it degrade the log's barriers to ordered OS-cache flushes that
survive a process crash, not power loss, as before #2818; encrypted preambles still require a
successful sync. A data file that answers "cannot sync" keeps commits durable in the WAL, which
is kept and grows up to `wal limit` (`$database.walKept`). On Unix, released .NET
runtimes report every fsync failure as success (dotnet/runtime#124725), so file
handles are synced natively (`fsync`; `F_FULLFSYNC` with `fsync` fallback on macOS)
and the raw errno decides between "cannot sync" and a failed sync.

Caller streams require their own correct I/O behavior. In-memory fault models do
not validate a real filesystem, controller or device cache. Real-file and process
tests complement them; platform evidence must state the actual runtime and host.
Linux results alone do not establish Windows sharing-lock or NLS behavior, nor
does compiling a Framework target establish that tests ran on CLR 4. See the
[CI matrix](../.github/workflows/_reusable-ci.yml) and
[runtime validation rules](rules/validation.md#runtime-and-environment-coverage).

Long-lived readers can keep the WAL nonempty indefinitely. Retirement reuses
payload slots but retains witness metadata; it does not bound growth or recovery
cost and does not punch holes. Shared readers require one-host mutex/file-sharing
coordination and intact lease files. Exclude concurrent writers and checkpoints
throughout backup preparation and capture. Under that exclusive control, capture
data and WAL consistently, or close readers, complete a checkpoint and verify an
empty WAL before copying the data file. An empty-WAL check is not a lock against
later writes, and a successful partial checkpoint does not make the data file
alone a complete backup.

For final acceptance, run relevant focused suites and broader regressions on the
integrated head, replay the pinned fuzz corpus, and execute the affected predecessor
probes. Use [development commands](rules/development.md) with consistent
`TestingEnabled=true`; use the [fuzz runner's](../LiteDB.Fuzz/README.md) bounded
smoke/determinism tier before longer campaigns. Preserve exact failure images,
seeds and binaries. Unexplained failures or missing safety evidence remain blockers;
elapsed campaign time, test counts and this document are not substitutes for those
results.
