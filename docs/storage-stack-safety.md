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
| No commit is written behind a torn frame. A failed write that may leave one (an overwrite of a slot, an append whose truncation failed or had to keep a header journal, a checkpoint's own WAL writes) stops the engine before the WAL writer is released, whatever the exception type; a failed append that was truncated releases its position, so the next append rewrites the slot. A caller stream other than a `MemoryStream` is flushed after each write under the lock its readers take, so a buffering one (a `BufferedStream`) holds no frame for a later write or a reader's seek to tear. A transaction whose safepoint write failed only rolls back (reads included), and a format promotion that fails with its header journal outstanding stops the engine. A committed legacy WAL page that is not a page of its type, or names a page beyond both files and every page a committed header of this database counts, fails the open, as does a committed header of another database. | [TornSlotRewrite_Tests](../LiteDB.Tests/Regressions/TornSlotRewrite_Tests.cs) (a commit forced into the window after the writer's release); [TornWalAppend_Tests](../LiteDB.Tests/Regressions/TornWalAppend_Tests.cs) (half-written frames, complete frames whose write reported failure, a BufferedStream and a stream that tears a held frame at the next write or the flush); [SharedBufferedLogStream_Tests](../LiteDB.Tests/Regressions/SharedBufferedLogStream_Tests.cs) (a reader in the window after a frame write); [CheckpointFailureWindow_Tests](../LiteDB.Tests/Regressions/CheckpointFailureWindow_Tests.cs) (failed retirement records; the journal guard behind the stop; the engine is closed); [FailedSafepointWrite_Tests](../LiteDB.Tests/Regressions/FailedSafepointWrite_Tests.cs); [FailedPromotionJournal_Tests](../LiteDB.Tests/Regressions/FailedPromotionJournal_Tests.cs); [LegacyWalPageBound_Tests](../LiteDB.Tests/Regressions/LegacyWalPageBound_Tests.cs). |
| Legacy checksum cutover is bounded and repeatable; mixed page coverage cannot become permission to ignore damaged checked pages. | [LazyChecksumCutover_Tests](../LiteDB.Tests/Internals/LazyChecksumCutover_Tests.cs), [LazyChecksumMigration_Tests](../LiteDB.Tests/Internals/LazyChecksumMigration_Tests.cs), [LazyChecksumMarker_Tests](../LiteDB.Tests/Internals/LazyChecksumMarker_Tests.cs); `checksum-page`, `checksum-migration`. |
| An in-place checkpoint or invalid-state header marker requires durable WAL and journal bytes even when ordinary commit sync has degraded. A failed sync must stop before data overwrites, preventing mixed old/new documents and stale indexes. Storage that answers "cannot sync" (#2242) proceeds in write order without a power-loss claim, as before #2818, and neither retires nor reuses WAL frames: every retiring checkpoint first syncs the data file and the log (the log's directory once per engine), and an engine syncs the raw log once before it first reuses a slot; an engine whose data is a file proves the data file before its first log sync (once per data header in the process; a file replaced at the same path with a byte-identical header is taken as the synced one, so whoever replaces it must sync it). A checkpoint that finds out during its own barriers keeps the frames it retired and publishes no root. After a data sync answered "cannot sync", every log sync (and a new WAL's directory entry) waits for a data sync that succeeds, so no log sync makes an emptied or converted WAL durable ahead of its unsynced backfill or header, also when the WAL alone syncs again; a full checkpoint whose backfill did not sync keeps a WAL whose frames the engine synced until the data file syncs (the OS could write the emptied WAL back first), otherwise it empties the WAL as where neither file syncs, and a stream opened to read never syncs. A legacy header beside checksummed WAL frames fails the open, changing neither file. | [CheckpointDurability_Tests](../LiteDB.Tests/Internals/CheckpointDurability_Tests.cs) rejects the padding and journal barriers on an I/O error, tests already-degraded and opted-out commits, checks zero data writes and stopped subsequent writes, and recovers both cached and durable images; [Issue2242_UnsyncableLog_Tests](../LiteDB.Tests/Issues/Issue2242_UnsyncableLog_Tests.cs) and [MvccUnsyncableLog_Tests](../LiteDB.Tests/Internals/MvccUnsyncableLog_Tests.cs) cover conversion, promotion, checkpoint and slot reuse on a log that never syncs or stops syncing during a checkpoint; [UnsyncableRetirement_Tests](../LiteDB.Tests/Regressions/UnsyncableRetirement_Tests.cs) checks that no witness is published and no slot cleared or reused when the data file, the log or its directory cannot sync; [RetiredSlotPowerLoss_Tests](../LiteDB.Tests/Regressions/RetiredSlotPowerLoss_Tests.cs) and [UnsyncedBackfillPowerLoss_Tests](../LiteDB.Tests/Regressions/UnsyncedBackfillPowerLoss_Tests.cs) recover each file as of its last successful sync after the data file stops syncing, with a second independent connection, also after neither file synced a full checkpoint and the WAL alone syncs again (documents and index compared); [DataFileStopsSyncing_Tests](../LiteDB.Tests/Regressions/DataFileStopsSyncing_Tests.cs) writes the WAL back ahead of the backfill, defers automatic checkpoints of a kept WAL, refuses a conversion whose drain keeps it, and checks that encrypted readers and a rebuild's installed file sync nothing unproven; [FreshEngineDurability_Tests](../LiteDB.Tests/Regressions/FreshEngineDurability_Tests.cs) covers a fresh engine (also over caller FileStreams) after a salt rotation whose data sync failed, a conversion and a rebuild where only the data file cannot sync, a single proof per data header, a file replaced at its path; [FreshEngineLogSync_Tests](../LiteDB.Tests/Regressions/FreshEngineLogSync_Tests.cs) keeps commits acknowledged durable when a fresh engine's first log sync repairs a torn tail or journals a checkpoint header after a truncation that never synced; [ConvertedWalBesideLegacyHeader_Tests](../LiteDB.Tests/Regressions/ConvertedWalBesideLegacyHeader_Tests.cs) pairs a 5.0.21 file with its conversion's WAL, plain and encrypted, writable and read-only. |
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
sync stops them. Storage that answers "cannot sync" (#2242), for the WAL or the data file, degrades them
to ordered OS-cache flushes that survive a process crash, not power loss, as before
#2818; encrypted preambles still require a successful sync. A file WAL is emptied only after
a data sync that covers its backfill succeeded, so there it is kept and grows
(`$database.walKept`), and a conversion or rebuild is refused unchanged. A WAL directory the process
cannot open for reading (EACCES/EPERM) counts as a directory that cannot sync. On Unix, released .NET
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
