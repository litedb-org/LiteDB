# Checksum merge review

PR #2954 is **not ready to merge**. The additional fault tests answer several
boundary questions, but reproduce a header-recovery gap. These are bounded crash
models, not a proof against every possible storage failure.

| Question | Test evidence | Answer |
| --- | --- | --- |
| Can checkpoint resume after a crash between page writes? | `EveryCheckpointWriteBoundary_RecoversAcknowledgedCommits` snapshots before every data write, including generation publication, and reopens with the original WAL. | Yes in the tested plain/encrypted cases; acknowledged collections survive replay, checkpoint, and another open. |
| Can checkpoint recover torn pages? | `TornCheckpointPages_RecoverExceptForTheUnprotectedHeader` retains only the first 512 bytes of each write. The collection map spans several sectors. | Non-header pages recover. Some header images cannot open, even with an intact WAL and `AutoRebuild=true`. Both writable and read-only opens are covered. |
| Can automatic conversion resume after interruption? | `LegacyConversion_CanResumeAtEveryPageWriteBoundary` uses v8/v9 images with legacy transaction fields and snapshots every conversion write, also retaining just its first sector. | Yes for those boundaries, plain and encrypted. This does not establish atomic header publication. |
| What if conversion tears inside the header's first sector? | `TornConversionHeader_IsDetectedButCannotResumeAutomatically` retains 64 or 128 bytes of the new header and the remainder of the old header. | Opening raises a checksum error and preserves the files. Automatic conversion cannot resume; no backup was created. |
| Can a later transaction's safepoint destroy an earlier acknowledged commit? | `TornSafepointAfterAnotherTransactionCommits_PreservesItsAcknowledgedPrefix` interleaves two writers, verifies the committed prefix is unchanged, tears the following safepoint, and reopens twice. | The acknowledged transaction survives; the interrupted transaction stays rolled back, plain and encrypted. |
| Are the checksum field, metadata, and reserved bytes covered? | `EveryByteOfDataPageAndWalTrailer_IsCovered` flips one bit at each data-page byte and each WAL-trailer byte. | Every tested mutation is rejected, including mutations of the checksum itself. |
| Can a valid page be accepted at the wrong data-file position? | `ValidDataPageChecksum_DoesNotPermitAMisdirectedPage`. | No; page identity is checked independently of CRC equality. |
| Does migration checksum unallocated padding or break later allocation? | `AutomaticConversion_PreservesUnusedPreallocation` converts a preallocated legacy image, checks untouched padding, then allocates and reopens new pages. | Padding remains zero and subsequent allocation/checkpoint works. |

## Blocking recovery gap

The data header is validated in `DiskService.ValidateExistingData` before
`WalIndexService.RestoreIndex` can read the WAL. A checkpoint can write a new
header CRC in its first sector while later collection-map sectors retain older
bytes. That correctly fails validation, but also prevents the intact committed
WAL from repairing the header. The new tests reproduce this using actual captured
checkpoint writes, including encrypted ciphertext, rather than arbitrary bit
corruption. Conversion has a related publication gap and no redundant header.

The tests asserting rejection are **characterization tests of the limitation**.
Their passing result does not mean crash recovery is fixed. Before merging,
header publication needs a durable recovery copy/protocol that preserves the
generation identity and works for caller-owned streams as well as files. The
acceptance test should then require all acknowledged records to recover from
these images without salvage, followed by a successful checkpoint and reopen.
Read-only recovery must still preserve both files, and stale WAL generations
must remain rejected.

Skipping header validation, recomputing a checksum over damaged metadata, or
silently rebuilding with potentially lost records is not an equivalent fix.
The automated review's suggestion to route checksum failures to automatic
rebuild does not resolve the durable-header problem. Current behavior deliberately
preserves the damaged files, including with `AutoRebuild=true`.

## Validation status

This review adds 19 test cases. The focused checksum, WAL power-loss, and
transaction-boundary suites pass **81 tests on .NET 8 and 81 on .NET 10**, with
no skips. No production recovery behavior is changed by this review.

On the initial PR commit, GitHub's Windows x64 .NET 10 test job hit its five-minute
step timeout with no failed test assertions reported before cancellation. That
result is not a successful suite run and is not assumed to be an unrelated flake.
A passing run remains a separate merge requirement.
