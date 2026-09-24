# Transaction and WAL regression coverage

The September 2026 review identified WAL amplification, unsafe finalization, and
transaction registration/lock leaks after cleanup errors. The tests below run
in the existing `LiteDB.Tests` CI suite; no separate opt-in test job is needed.

## WAL durability and isolation

`WalSlotReuse_Tests` covers the original 50,000 random-GUID document workload
with a measured peak-WAL limit of 1.5 times the data-file size. It also checks
cache replacement, confirmation-page append ordering, commit/rollback recovery,
and partial overwrite failures, including encrypted streams.

`WalTransactionBoundary_Tests` adds:

- 32 repeated updates and safepoints with identical WAL positions and length.
- Commit immediately after the final dirty page has been flushed.
- Recovery of stream images captured before engine disposal, both before and
  after confirmation; unconfirmed updates must stay invisible.
- Checkpoint followed by a second open, validating the resulting data file.
- Two overlapping transactions on different threads and collections, with the
  later-started transaction committed first and the first committed or rolled back.
- A reader retaining an older committed WAL version while another thread reuses
  slots and commits newer values. Fresh readers must see the new version.
- Read-only and unchanged write snapshots committing without appending WAL bytes.
- Failed confirmation after the final flush, checking frame accounting,
  unchanged log length, rollback, and recovery of the previous committed values.

The recovery helper copies the live data/log bytes into independent expandable
streams. It does not close or roll back the original engine before copying.
Tests check the complete expected document-ID set as well as values. Encrypted
and unencrypted streams exercise the durability and isolation cases.

The final-safepoint test exposed a missing confirmation: a transaction with no
remaining dirty pages returned from `Commit()` without publishing its earlier
WAL writes. Commit now appends a confirmed copy of a previously flushed page.
Four new cases failed before that correction and passed afterward.

## Cleanup and finalization

`TransactionCleanup_Tests` covers the original stale-lease release failure,
200 subsequent queries, exclusive checkpoint acquisition, and a write from a
different thread. GC coverage includes an exited transaction thread, explicit
engine cleanup, and an unreachable engine containing a damaged transaction.

`TransactionCleanupBoundary_Tests` adds:

- Failing either an explicit or query-only transaction while another transaction
  on the same thread survives. Registration, thread-slot state, and the shared
  transaction lock are checked before and after releasing the survivor.
- 200 consecutive cleanup failures for both read and write leases, alternating
  explicit and query-only transactions, with frame counts and follow-up queries.
- `Dispose(false)` preserving healthy read/write leases and registration.
- Monitor shutdown continuing through seven healthy transactions after one
  damaged transaction, rejecting new work, and tolerating repeated disposal.
- Cleanup continuing across damaged, writable, and readable snapshots in one
  transaction, followed by writes from another thread to every collection.

The thread interactions use completion signals or bounded joins, not sleeps to
guess when another thread has acquired a lock.

## Test effectiveness

The following source mutations were applied temporarily and individually during
validation. Each caused test assertions to fail; compilation failures were not
accepted as evidence. The original source was restored after every mutation.

| Reintroduced defect | Regression that failed |
| --- | --- |
| Append every safepoint page | `RepeatedSafepoints_ReusePositions_AndCommitAfterTheLastFlush` |
| Overwrite a confirmation in its old slot | `WriteLogDisk_ReusesOnlyUnconfirmedSlots_AndRefreshesCachedBytes` |
| Omit confirmation after the final flush | `RepeatedSafepoints_ReusePositions_AndCommitAfterTheLastFlush`, `FailedConfirmationAfterTheLastFlush_ReleasesItsFrameAndRemainsUncommitted` |
| Remove registration only after successful disposal | `RepeatedCleanupFailures_DoNotExhaustRegistrationOrRetainFrames` |
| Unlock while a second transaction survives | `FailedRelease_PreservesAnotherTransactionAndItsLock` |
| Run managed cleanup from `Dispose(false)` | `DisposeFalse_PreservesHealthyManagedLeasesAndRegistration` |

Run the boundary regressions with:

```shell
dotnet test LiteDB.Tests/LiteDB.Tests.csproj -c Release -f net8.0 -p:TestingEnabled=true --settings tests.runsettings --filter "FullyQualifiedName~WalTransactionBoundary_Tests|FullyQualifiedName~TransactionCleanupBoundary_Tests"
```

Local validation on Windows x64: the Release solution build passes, and the
full .NET 8.0.30 and .NET 10.0.11 suites each pass 556 tests with 7 existing skips.
The two boundary fixtures add 22 test cases to the existing suite.
