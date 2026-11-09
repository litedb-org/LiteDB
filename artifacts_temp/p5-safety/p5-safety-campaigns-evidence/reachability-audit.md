# Registered fault-point audit

Recorded marker hits establish reachability. xUnit execution is additional evidence, not marker telemetry. Zero-hit entries remain explicitly unreached by telemetry.

| Kind | Point | Hits | Reachability | Registered test execution (pass/fail/skip) |
| --- | --- | ---: | --- | --- |
| hooks | after-reclaim | 615636 | reached | {'Passed': 24} |
| hooks | before-commit-lock | 693153 | reached | {'Passed': 84} |
| hooks | before-index-lock | 693153 | reached | {'Passed': 3} |
| hooks | before-reclaim | 622022 | reached | {'Passed': 36} |
| hooks | before-wal-reclaim-lock | 42374 | reached | {'Passed': 24} |
| hooks | data-flushed | 670726 | reached | {'Passed': 24} |
| hooks | data-page | 13000632 | reached | {'Passed': 30} |
| hooks | retirement-after-header-write | 42692 | reached | {'Passed': 216} |
| hooks | retirement-after-record-write | 123117 | reached | {'Passed': 216} |
| hooks | retirement-before-header-write | 45552 | reached | {'Passed': 246} |
| hooks | retirement-before-journal-retire-flush | 45702 | reached | {'Passed': 234} |
| hooks | retirement-before-record-write | 124749 | reached | {'Passed': 234} |
| hooks | retirement-header-flushed | 42624 | reached | {'Passed': 300} |
| hooks | retirement-journal-retired | 45634 | reached | {'Passed': 216} |
| hooks | retirement-records-flushed | 45618 | reached | {'Passed': 216} |
| hooks | wal-slot-cleared | 12661859 | reached | {'Passed': 258} |
| hooks | wal-slots-flushed | 42290 | reached | {'Passed': 258} |
| hooks | wal-slots-published | 42202 | reached | {'Passed': 258} |
| hooks | created | 20806 | reached | {'Passed': 12} |
| hooks | flushed | 20806 | reached | {'Passed': 24} |
| hooks | published | 20806 | reached | {'Passed': 84} |
| hooks | publishing | 20806 | reached | {'Passed': 12} |
| hooks | retired | 21190 | reached | {'Passed': 9} |
| hooks | written | 20806 | reached | {'Passed': 24} |
| hooks | checkpoint-after-clear | 616138 | reached | {'Passed': 120} |
| hooks | checkpoint-after-data-flush | 671324 | reached | {'Passed': 336} |
| hooks | checkpoint-after-page-write | 13001195 | reached | {'Passed': 336} |
| hooks | checkpoint-before-clear | 616748 | reached | {'Passed': 120} |
| hooks | checkpoint-before-data-flush | 672610 | reached | {'Passed': 336} |
| hooks | checkpoint-before-page-write | 13004527 | reached | {'Passed': 336} |
| hooks | promotion-after-header-flush | 27744 | reached | {'Passed': 219} |
| hooks | promotion-after-header-write | 27832 | reached | {'Passed': 432} |
| hooks | promotion-after-journal-flush | 29889 | reached | {'Passed': 432} |
| hooks | promotion-after-journal-page-write | 61743 | reached | {'Passed': 222} |
| hooks | promotion-after-journal-retire-flush | 27554 | reached | {'Passed': 432} |
| hooks | promotion-before-header-write | 29766 | reached | {'Passed': 408} |
| hooks | promotion-before-journal-retire-flush | 27611 | reached | {'Passed': 216} |
| hooks | promotion-before-journal-write | 31992 | reached | {'Passed': 612} |
| hooks | promotion-recovery-after-header-flush | 57026 | reached | {'Passed': 18} |
| hooks | promotion-recovery-after-header-write | 11128 | reached | {'Passed': 18} |
| hooks | promotion-recovery-before-header-write | 18456 | reached | {'Passed': 18} |
| hooks | startup-after-tail-trim | 3904 | reached | {'Passed': 177} |
| hooks | startup-before-tail-trim | 3904 | reached | {'Passed': 177} |
| hooks | wal-after-durable-flush | 2454928 | reached | {'Passed': 240} |
| hooks | wal-after-index-confirmation | 2370463 | reached | {'Passed': 33} |
| hooks | wal-before-durable-flush | 2455653 | reached | {'Passed': 336} |
| hooks | wal-before-index-confirmation | 2370753 | reached | {'Passed': 72} |
| hooks | wal-confirmation-after-write | 2459911 | reached | {'Passed': 228} |
| hooks | wal-confirmation-before-write | 2460448 | reached | {'Passed': 216} |
| hooks | wal-page-after-write | 37030077 | reached | {'Passed': 222} |
| hooks | wal-page-before-write | 37033053 | reached | {'Passed': 222} |
| hooks | after-log-backup | 51484 | reached | {'Passed': 180} |
| hooks | after-source-backup | 46084 | reached | {'Passed': 180} |
| hooks | after-temp-install | 40724 | reached | {'Passed': 180} |
| hooks | before-candidate-cleanup | 16120 | reached | {'Passed': 144} |
| hooks | before-candidate-republish | 0 | unreached (no marker telemetry; xUnit execution listed separately) | {'Passed': 156} |
| hooks | before-candidate-rollback | 16120 | reached | {'Passed': 147} |
| hooks | before-log-backup | 54184 | reached | {'Passed': 180} |
| hooks | before-log-rollback | 16120 | reached | {'Passed': 156} |
| hooks | before-recovery-marker | 54184 | reached | {'Passed': 156} |
| hooks | before-recovery-marker-delete | 54184 | reached | {'Passed': 150} |
| hooks | before-recovery-marker-flush | 54184 | reached | {'Passed': 150} |
| hooks | before-source-backup | 48784 | reached | {'Passed': 180} |
| hooks | before-source-retraction | 0 | unreached (no marker telemetry; xUnit execution listed separately) | {'Passed': 156} |
| hooks | before-source-rollback | 16120 | reached | {'Passed': 147} |
| hooks | before-temp-install | 43384 | reached | {'Passed': 180} |
| hooks | wal-rebase-before-anchor-flush | 0 | unreached (no marker telemetry; xUnit execution listed separately) | {'Passed': 48} |
| hooks | wal-rebase-before-rewrite | 0 | unreached (no marker telemetry; xUnit execution listed separately) | {'Passed': 48} |
| hooks | wal-before-batch-flush | 11695843 | reached | {'Passed': 60} |
| hooks | wal-before-failed-append-truncate | 4721 | reached | {'Passed': 6} |
| hooks | wal-after-failed-append-truncate | 1852 | reached | {'Passed': 6} |
| hooks | wal-before-ticket-completion | 850636 | reached | {'Passed': 60} |
| hooks | offline-validated | 0 | unreached (no marker telemetry; xUnit execution listed separately) | {'Passed': 27} |
| hooks | offline-retired | 0 | unreached (no marker telemetry; xUnit execution listed separately) | {'Passed': 27} |
| hooks | offline-readers-retired | 0 | unreached (no marker telemetry; xUnit execution listed separately) | {'Passed': 27} |
| hooks | offline-complete | 0 | unreached (no marker telemetry; xUnit execution listed separately) | {'Passed': 27} |
| hooks | handle-before-release | 2400 | reached | {'Passed': 108} |
| hooks | handle-after-release | 2400 | reached | {'Passed': 108} |
| injectors | DarwinTruncated | 0 | unreached (no marker telemetry; xUnit execution listed separately) | {'Passed': 3} |
| injectors | DispatchOverride | 0 | unreached (no marker telemetry; xUnit execution listed separately) | {'Passed': 18} |
| injectors | OwnershipStage | 115547 | reached | {'Passed': 66} |
| injectors | WriteRetry | 0 | unreached (no marker telemetry; xUnit execution listed separately) | {'Passed': 6} |
| injectors | BeforeTransactionRegistration | 0 | unreached (no marker telemetry; xUnit execution listed separately) | {'Passed': 18} |
| injectors | SimulateDiskWriteFail | 39494033 | reached | {'Passed': 30} |
| injectors | SimulateDiskReadFail | 227268267 | reached | {'Passed': 18} |
| injectors | SimulateDataWriteFail | 13004527 | reached | {'Passed': 18} |
| injectors | SimulateErrno | 2690610 | reached | {'Passed': 27} |
| injectors | SimulateDirectoryErrno | 167120 | reached | {'Passed': 54} |
| injectors | SimulatePosixDelete | 25626 | reached | {'Passed': 3} |
| injectors | AfterFailedWalWrite | 3926 | reached | {'Passed': 63} |
| injectors | BootstrapStage | 0 | unreached (no marker telemetry; xUnit execution listed separately) | {'Passed': 219} |
| injectors | AllocateFile | 0 | unreached (no marker telemetry; xUnit execution listed separately) | {'Passed': 3} |
| injectors | MapFile | 0 | unreached (no marker telemetry; xUnit execution listed separately) | {'Passed': 3} |
| injectors | PublicationStage | 1249635 | reached | {'Passed': 210} |
| injectors | AdmissionStage | 561044 | reached | {'Passed': 51} |
| injectors | RequestStage | 104373 | reached | {'Passed': 24} |
| injectors | SimulateWaitError | 20000 | reached | {'Passed': 2, 'NotExecuted': 4} |
| injectors | MaintenanceStage | 0 | unreached (no marker telemetry; xUnit execution listed separately) | {'Passed': 126} |
| injectors | MappedRebuildStage | 0 | unreached (no marker telemetry; xUnit execution listed separately) | {'Passed': 192} |
| injectors | FinalCloseStage | 209860 | reached | {'Passed': 78} |

## All unreached markers

- `api:ILiteTransaction.CollectionExists`
- `api:ILiteTransaction.GetCollectionNames`
- `api:LiteTransactionExtensions.BeginTransaction`
- `fault-point:AllocateFile`
- `fault-point:BeforeTransactionRegistration`
- `fault-point:BootstrapStage`
- `fault-point:DarwinTruncated`
- `fault-point:DispatchOverride`
- `fault-point:MaintenanceStage`
- `fault-point:MapFile`
- `fault-point:MappedRebuildStage`
- `fault-point:WriteRetry`
- `fault-point:before-candidate-republish`
- `fault-point:before-source-retraction`
- `fault-point:offline-complete`
- `fault-point:offline-readers-retired`
- `fault-point:offline-retired`
- `fault-point:offline-validated`
- `fault-point:wal-rebase-before-anchor-flush`
- `fault-point:wal-rebase-before-rewrite`
- `maintenance:direct-finalizer-release-queued`
- `maintenance:invalid-datafile-state-close`
- `maintenance:operation-refused-by-close`
- `maintenance:shared-dispose-during-active-call`
- `maintenance:shared-dispose-during-transaction`
- `maintenance:shared-dispose-ends-foreign-ownership`
- `maintenance:shared-dispose-with-leased-reader`
- `maintenance:shared-dispose-with-mutex-waiters`
- `refusal:checkpoint-redo-damaged`
- `refusal:checkpoint-wal-verification-failed`
- `refusal:collation-mismatch`
- `refusal:coordinator-outcome-unknown`
- `refusal:coordinator-transaction-aborted`
- `refusal:direct-attach-during-maintenance`
- `refusal:encrypted-wal-foreign-preamble`
- `refusal:explicit-commit-foreign-thread`
- `refusal:index-migration-duplicate-key`
- `refusal:index-migration-invalid-key`
- `refusal:index-migration-limit-size`
- `refusal:legacy-tail-after-publication`
- `refusal:legacy-tail-unrecognized`
- `refusal:mapper-root-not-document`
- `refusal:rebuild-incomplete-marker`
- `refusal:retirement-sequence-not-reached`
- `refusal:shared-abandoned-transaction`
- `refusal:shared-call-after-dispose`
- `refusal:shared-commit-foreign-thread`
- `refusal:shared-file-locking-disabled`
- `refusal:shared-peer-waits-for-own-ownership`
- `refusal:shared-rebuild-with-open-readers`
- `refusal:shared-teardown-reentry`
- `refusal:shared-teardown-reentry-after-dispose`
- `refusal:unsupported-file-version`
- `refusal:vector-near-mismatch`
- `refusal:vector-score-aggregate`
- `refusal:vector-score-without-search`
- `refusal:vector-wherenear-repeated`
- `refusal:wal-append-during-header-publication`
