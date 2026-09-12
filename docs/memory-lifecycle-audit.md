# Memory ownership follow-up audit

This audit covers the memory-management PR and the resource ownership paths it
uses: cache frames, transaction/snapshot construction, query cursors, sort storage,
stream pools, cipher wrappers, and pooled serialization buffers. Corrections are
developed on `bug/memory-leaks-followup` and integrated into `bug/memory-leaks`.
The integration preserves the PR's WAL reservation rollback and rejects transaction
registration during shutdown without adding a transaction-registry lock.

## Failure paths corrected

| Ownership boundary | Correction | Verification |
| --- | --- | --- |
| Readable-to-writable copy | Pin the source before acquiring a destination; return the destination on copy failure. | `FrameFailure_Tests`: full cache with a pinned competing frame, repeated injected copy failures. |
| WAL publication | Include publication in cleanup; discard unpublished writable frames, release published pins. | Publication collision plus existing write/callback failure tests. |
| Disk read hooks | Return the acquired writable/readable frame when a failure is injected after reading. | Twenty failures per frame mode with zero outstanding ownership. |
| Snapshot construction | Release partially created collection/index buffers and the collection lock if construction fails. | Twenty collection creations failing at the file-size limit. |
| Page decoding | Release frames when typed page construction or validation fails before local registration. | Twenty wrong-page-type reads in each snapshot mode. |
| First query result | Dispose the source when initial result transformation fails in the reader constructor. | 110 failed queries with an empty transaction registry after each. |
| Stream-pool shutdown | Drain late returns and reject late rentals; publish writer ownership separately from `Lazy.IsValueCreated`. | Deterministic creation/disposal races for readers and writers; late-return checks. |
| Shared base streams | Let the factory own the base stream; all per-reader/writer wrappers leave it open. | Exactly one base-stream disposal and retained caller ownership. |
| Cipher lifetime | Release both crypto streams, transforms, cipher and base stream, including partial construction and disposal errors. | Constructor I/O failure, repeated disposal, throwing base disposal; existing encrypted read/write tests. |
| Sorted output | Return pooled arrays in iterator `finally`; make sorter disposal return storage slots once and clear retained buffers. | Early stop, read failure, and repeated disposal with unique storage positions. |
| Serialization | Return temporary arrays on numeric, ObjectId and string failures; dispose failing constructor enumerators. | Tracking array pools and failing enumerators on both modern and legacy implementations. |
| Upgrade/recovery | Return the upgrade probe array on the normal non-upgrade early exit and return recovery arrays on I/O failure. | Review of every `ArrayPool.Rent` site and full build/tests. |

The direct frame and stream-pool regressions were run against the previous
implementation and failed before correction. The first-result transform test
also reproduced a registered transaction left behind by the failed constructor.

The BSON element decoder is now a separate helper used by `BufferReader`, keeping
serialization responsibilities clear and the modified buffer files below the
500-line limit. No persisted format or public API was added for this extraction.
Optional internal array-pool dependencies allow tests to count actual rentals and
returns instead of inferring leaks from process memory.

## Verification

- Full Release solution build, including .NET Standard 2.0 and .NET 8 library targets.
- .NET 8 and .NET 10: 464 passed, 7 skipped on each target.
- .NET Framework 4.6.1 and 4.8.1: 463 passed, 8 skipped on each target, using
  `xunit.console.exe` from `xunit.runner.console` 2.9.2. The local `dotnet test`
  adapter discovers no legacy tests; its exit code alone is not a successful run.
- Production builds disable `TESTING`. The 200,000-document acceptance workload
  was rerun on both .NET 8 and .NET 10; reports are in
  `memory-measurements/lifecycle-net*-64-200k.json`. These include sorted scans,
  concurrent lookups, index building, bulk writes, encrypted updates, vector
  queries, and repeated release under an 8 MiB pressure profile. Cache ownership
  counters finish at zero and overflow segments are reclaimed.
- CI supports `workflow_dispatch`, so the separate branch can run the complete
  existing build/test/reproducer matrix without changing PR #2772. Use
  `gh workflow run ci.yml -R JKamsker/LiteDB --ref bug/memory-leaks-followup` and
  inspect the run's commit SHA and every job before treating it as verification.

The revision-6 performance matrix remains historical evidence for that revision.
The new production reports are acceptance reruns, not a new claim of throughput
parity. The core frame monitor still protects page lifetimes; the previously
removed admission locks remain removed. No new locks were added by this audit.
The independently reproduced HNSW result-count issue is unrelated to memory
ownership and remains documented in `memory-management-validation.md`.
