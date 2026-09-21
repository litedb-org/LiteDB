# Checksum merge review

The header-recovery gap is covered by the implemented
[header-publication protocol](header-publication.md). Tests require successful
recovery and another checkpoint/open, rather than merely expecting corruption
errors. The matrix below describes bounded fault models; successful storage
syncs must persist bytes.

| Question | Test evidence | Answer |
| --- | --- | --- |
| Can checkpoint certify part of a damaged live WAL transaction? | `CorruptCommittedWal_IsRejectedBeforeCheckpointChangesAnyData` and `StaleSafepointFrames_CannotBeCertifiedByCheckpoint`. | A full preflight rejects damaged CRCs, counts, digests, sequences, missing/truncated confirmations, and genuine stale safepoint frames before changing either file. Subsequent writes stop; recovery and another checkpoint retain only the valid prefix. |
| What if WAL damage occurs after checkpoint preflight or its first data write? | `WalChangesDuringJournalBinding_AreRejectedBeforeDataWrites`, `WalDamageAfterDataCopyStarts_CannotExposeAPartialTransaction`, and `PublishedGeneration_DoesNotRequireTheOldCheckpointWal`. | The journal validates the exact WAL bytes it binds. Reopening with the old generation rejects changed redo and preserves both files; a verified newer generation can ignore obsolete damaged redo. Both plain/encrypted cases are covered. |
| Can Shared mode lose the recovery report before it is queried? | `SharedReopen_PreservesTheLastRecoveryReport`. | The owner retains the last nonempty report across engine reopenings and checkpoint. A new independent owner starts fresh. Invalid/partial frames are distinguished from intact unconfirmed tails. |
| What if an append shares the acknowledged confirmation's physical sector? | `SectorTearsOfNewAppends_PreserveAcknowledgedPrefix` uses 512/4096-byte boundaries, plain/encrypted files, read-only recovery, and checkpoint/reopen with full documents and index lookups. | Torn new bytes reject the entire later transaction even if its confirmation persists. Previously synced bytes outside the addressed write range must survive (power-safe overwrite). |
| Can checkpoint resume between page writes? | `EveryCheckpointWriteBoundary_RecoversAcknowledgedCommits` captures data and WAL before every write. | Acknowledged collections survive replay, checkpoint, and reopen, plain and encrypted. |
| Can checkpoint recover torn headers and other pages? | `TornCheckpointPages_IncludingHeaders_RecoverAcknowledgedCommits` tears actual captured writes, including a collection map spanning sectors. | Recovery succeeds at tested cuts from 1 to 4096 bytes. Read-only opens preserve both images. |
| Can repeated checkpoints recover a changing database rather than only fresh inserts? | `RepeatedGenerations_WithPageReuseAndSectorTears_PreserveCommittedModel` runs six generations of deterministic insert/update/delete/rollback transactions with forced safepoints and variable-size overflow documents. | Full documents, nonunique and unique index lookups match an independent model after alternating 16/512-byte sector tears, out-of-order page persistence, read-only recovery, new writes, checkpoint, and reopening. |
| Can conversion recover when its initial legacy checkpoint tears? | `LegacyRecoveryCheckpointAndConversion_CanBothTearDuringWritableOpen` starts with acknowledged legacy WAL updates and a newly created collection. | Every captured data write during the legacy checkpoint and conversion can tear at byte 15; v8/v9, plain/encrypted, read-only and writable recovery preserve both collections. |
| Can conversion resume after torn data writes? | `LegacyConversion_CanResumeAtEveryPageWriteBoundary` covers v8/v9 and cuts inside encrypted blocks. | Verified legacy redo restores the original data before conversion is retried. |
| What if the conversion header is torn or fully written before cleanup? | `TornConversionHeader_ResumesAutomatically` tests 64-byte, 128-byte, and whole-page publication. | Read-only recovery and writable conversion preserve all documents. |
| Can creating the conversion backup itself publish partial data? | `EveryConversionRedoWrite_CanTearWithoutPublishingPartialData` captures every intent/redo/footer write, testing partial tails and zero-extended pages at multiple cuts. | Incomplete backup is ignored; verified preparation can recover even when the final confirmation tears. |
| Does a failed sync allow data to change early? | `JournalMustBeDurableBeforeCheckpointTouchesData` and `FailedConversionBackupSync_CannotPublishAPartialLegacyTransaction`. | Ordinary I/O failures stop the operation and preserve original data. Conversion cannot proceed without durable redo. |
| Can repair/cleanup failure destroy the remaining recovery copy? | `FailedHeaderRepair_PreservesRecoveryJournalForAnotherOpen` and `InterruptedJournalRemoval_KeepsTheSyncedHeaderAndRedo`. | A subsequent open recovers after failed writes, syncs, and truncation. |
| Does real file-backed recovery agree with stream tests? | `FileBackedRecovery_PreservesReadOnlyImagesAndRepairsWritableOpens`. | Both plain and encrypted files recover; read-only bytes remain unchanged. |
| Can a damaged journal certify a damaged header? | `CorruptedRecoveryCopy_CannotCertifyADamagedHeader`. | No; opening fails and preserves both sources. |
| Does rebuild use the same recovery copy? | `RebuildReader_UsesJournalWithoutMutatingTheSources`. | It recovers the documents without salvage errors or source mutation. |
| Can a later safepoint destroy an acknowledged commit? | `TornSafepointAfterAnotherTransactionCommits_PreservesItsAcknowledgedPrefix`. | The committed prefix remains immutable; the other transaction remains rolled back. |
| Are data bytes, checksum fields, and WAL metadata covered? | `EveryByteOfDataPageAndWalTrailer_IsCovered`. | A one-bit mutation at every tested byte is rejected. |
| Can a valid page be accepted at the wrong position? | `ValidDataPageChecksum_DoesNotPermitAMisdirectedPage`. | No; identity is checked independently of CRC equality. |
| Does migration preserve unused preallocation? | `AutomaticConversion_PreservesUnusedPreallocation`. | Padding stays zero and later allocation/checkpoint/reopen works. |
| Can an older engine append commits after interrupted conversion? | `LegacyCommitsAfterAConversionFooter_AreReplayedBeforeConversion` and the separate-process 5.0.21 compatibility runner. | Later commits survive read-only recovery, writable conversion, and reopening, with and without a legacy checkpoint. |
| Can encrypted disposal undo WAL truncation? | `DisposingAfterCheckpoint_DoesNotReextendTheTruncatedWal`. | The truncated writer position is clamped before CryptoStream disposal. |
| Do CI platform labels describe the process executing the tests? | `RequestedRuntimeAndArchitecture_AreActuallyRunning` and `LoadedLibrary_ContainsTheRequiredEngineTestHooks`, required by `scripts/run-ci-tests.ps1` even for filtered runs. | Each modern-runtime job pins an isolated test host and asserts its runtime major and architecture. Windows x86 selects an x86 host; Linux ARM64 uses a native runner. A mismatched runtime was observed to fail the guard during local validation. |

Run the focused matrix with:

```sh
dotnet test LiteDB.Tests -c Release -f net8.0 -p:TestingEnabled=true --settings tests.runsettings --filter 'FullyQualifiedName~Checksum|FullyQualifiedName~HeaderJournal|FullyQualifiedName~ConversionJournalCrash|FullyQualifiedName~LegacyJournalTail|FullyQualifiedName~WalTransactionBoundary|FullyQualifiedName~WalPowerLoss|FullyQualifiedName~CheckpointWalBinding|FullyQualifiedName~WalRecoveryReport'
```

Use `-f net10.0` for the second runtime. The compatibility script additionally
runs the released LiteDB 5.0.21 in another process against interrupted conversion,
normal conversion, encrypted files, and the v10 rejection boundary. CI must pass
on the final PR commit before merging; superseded runs do not substitute for
that gate.

The shared-mode CI tests use concurrent tasks, not separate processes. Their
workers await completion without nested blocking thread-pool waits and retain the
30-second deadline and document-count assertions. `StalledWorker_TimesOutBeforeCompletion_AndDefersFileCleanup`
verifies that a worker which ignores cancellation cannot block reporting failure;
fixture cleanup waits asynchronously until workers release the files. The legacy
compatibility runner does launch separate processes.
Do not infer broader process-locking coverage from the shared-mode test names.

## Acceptance boundaries

The upstream issue's six requirements are covered by transaction/frame corruption
and power-loss tests (complete transactions and a verified prefix), generation
and slot-reuse tests (stale frames), recovery diagnostics assertions (visible
truncation), data-page validation tests (the additional requested scope), the
separate-process legacy compatibility runner (format boundary and encryption),
and the insert/bulk plus maintenance benchmarks (cost). Automatic writable
conversion is the chosen compatibility policy; read-only opens never convert.

Fault tests distinguish writes from successful syncs. The mixed-workload model
also persists a complete checkpoint header ahead of some data pages, before the
data-sync barrier. It never loses data behind a successful sync while retaining
a later salt publication: storage violating that contract is outside the
promised durability model. CRC32C is probabilistic accidental-corruption
detection, not cryptographic integrity. Passing this matrix increases confidence;
it does not prove every device behavior or arbitrary independent damage is
recoverable. Final-commit CI across the supported runtime/OS matrix remains a
merge gate.
