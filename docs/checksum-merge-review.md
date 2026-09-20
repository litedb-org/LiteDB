# Checksum merge review

The header-recovery gap is covered by the implemented
[header-publication protocol](header-publication.md). Tests require successful
recovery and another checkpoint/open, rather than merely expecting corruption
errors. The matrix below describes bounded fault models; successful storage
syncs must persist bytes.

| Question | Test evidence | Answer |
| --- | --- | --- |
| Can checkpoint resume between page writes? | `EveryCheckpointWriteBoundary_RecoversAcknowledgedCommits` captures data and WAL before every write. | Acknowledged collections survive replay, checkpoint, and reopen, plain and encrypted. |
| Can checkpoint recover torn headers and other pages? | `TornCheckpointPages_IncludingHeaders_RecoverAcknowledgedCommits` tears actual captured writes, including a collection map spanning sectors. | Recovery succeeds at tested cuts from 1 to 4096 bytes. Read-only opens preserve both images. |
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

Run the focused matrix with:

```sh
dotnet test LiteDB.Tests -c Release -f net8.0 -p:TestingEnabled=true --settings tests.runsettings --filter 'FullyQualifiedName~Checksum|FullyQualifiedName~HeaderJournal|FullyQualifiedName~ConversionJournalCrash|FullyQualifiedName~LegacyJournalTail|FullyQualifiedName~WalTransactionBoundary|FullyQualifiedName~WalPowerLoss'
```

Use `-f net10.0` for the second runtime. The compatibility script additionally
runs the released LiteDB 5.0.21 in another process against interrupted conversion,
normal conversion, encrypted files, and the v10 rejection boundary. CI must pass
on the final PR commit before merging; an earlier Windows test timeout is not
counted as a successful run.
