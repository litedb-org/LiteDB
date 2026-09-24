# Shared read performance

After the storage stack (#2998, #2924, #2999, #3000), shared-mode reads were slower than before it (`0fd277aae`). Direct-mode reads barely changed: a point read went from 0.036 to 0.038 ms. The extra cost therefore lies in what shared mode repeats for every operation, not in the query engine or in checksum validation. This document summarizes the investigation, the changes in #3003 and what remains.

Two investigations reached the same diagnosis independently: one on Windows, and one on Linux in the draft #3009, whose fingerprint cache is part of #3003 now. The workloads use a 2,000-document fixture:
- point read: `FindById`;
- full scan: all 2,000 rows;
- mixed: one update per nine point reads;
- update loop.

## Findings

1. **Engine startup repeated work whose result could not have changed.** A cold point read spent about 68% of its time opening an engine:
   - The page-0 header was read and validated twice: by `DiskService.ValidateExistingData` and again by `LiteEngine.Open`.
   - `ValidateCollationStamp` ran three times per open: before, during and after WAL restore. Each run computed the collation fingerprint, which writes 361 culture comparisons and hashes them with SHA-256. On Linux this fingerprint was 26% of the sampled time of a point-read trace. It arrived with the persisted index compatibility changes, so attributing the regression to page checksums alone misses it.
   - With auto-rebuild enabled, every operation also scanned the reader-lease directory (`OldestVersion`).
2. **A streaming query ran twice.** `TryBufferResult` executed the query on the writable engine and buffered up to 100 values or 64 KiB. On overflow it discarded that work, registered a lease, opened a second (snapshot) engine with a cold page cache and executed the query again from the start.
3. **Lease registration was a filesystem round trip.** Leases were not deleted on close. The next registration therefore enumerated the `-readers` directory, opened the previous lease exclusively to prove it dead, deleted it and the directory, recreated the directory and created the new lease. An absent directory was detected through a caught `DirectoryNotFoundException`.
4. **Smaller costs.** The handle identity check of the Windows handle cache takes about 14% of an open. The mutex holder thread from #3006 adds about 20–30 µs per operation, and the rebuild-marker probe a few percent. Each of these safety mechanisms was kept.
5. **Lazy checkpoints move some work to reads.** After a write, up to 50 WAL pages stay in the WAL, and each following operation's fresh engine restores them. On Linux, WAL index restoration appeared in 15.1% of the mixed-workload trace. In the steady-state measurements below, the mixed workload already matches pre-stack dev without further changes. A checkpoint on the first read after writes would cost four syncs plus the page copies per update, so it was not added.
6. **Cold runs mostly measure JIT warm-up.** The post-stack code is larger. In short, cold runs it compiled 2.3× more methods in the second half of the run, including `Crc32C`. With the tiered call-counting delay set to 0, a pre-stack scan takes 3.1 ms and the new code 3.5 ms. CRC validation is about 1.3–1.8 ms per cold scan, but 15 µs per scan in steady state (0.7 µs per page, 11.5 GB/s). The first benchmark published on #3003 used such cold runs and overstated the steady-state gap.

## Changes in #3003

- **The live-reader check for auto-rebuild** runs only when an invalid-state header is about to be rebuilt, still under the mutex. A live reader still blocks the rebuild.
- **Bounded collation fingerprint cache** (from #3009):
  - At most 64 immutable, process-local runtime results, keyed by comparer name, LCID, comparison options and sort version, so equal LCIDs alone do not identify a comparer.
  - A collision recomputes. A runtime that cannot describe its sort tables returns 0 and is asked again on the next request.
  - The cache holds the runtime's answer, never a database's verdict: every loaded data and WAL header is still compared against it, and the persisted fingerprint bytes are unchanged.
- **Header reuse:** the header that `ValidateExistingData` read, recovered and validated is handed to `LiteEngine.Open` once. It is the same engine, still opening, so nothing can write in between. New files still use `ReadFull`.
- **One execution per read:** a pure read (no transaction, `FOR UPDATE` or `SELECT INTO`) opens the read-only snapshot engine directly under the mutex.
  - A result within 100 values / 64 KiB finishes there.
  - A larger one registers its lease for that engine's read version while the mutex is still held, then continues the same reader through `PrefixedDataReader`.
  - The writable engine is not detached, because its close may delete an empty WAL and the shared `-tmp` file, which outside the mutex could delete a WAL another process just wrote.
  - Files that need a writable open first still go through the writable engine within the same mutex hold: missing files, a pending upgrade, index migration, promotion and auto-rebuild.
- **Self-deleting leases:** a lease is an exclusive handle created with `DeleteOnClose`, and registration only checks that the registry is readable.
  - A held lease cannot be taken by another process's exclusive probe (a sharing violation on Windows, `LOCK_EX` on Unix), and .NET deletes it on Unix only at `Dispose`.
  - Checkpoints still remove crashed readers' leases and fail closed on a registry they cannot read.

No persistence format, checksum, checkpoint fence or ownership rule changed.

## Results

**Windows, steady state** (AMD Ryzen 9 9955HX, NVMe, .NET 10; 5 s warm-up, then 3 interleaved rounds; median ms per operation):

| Workload | pre-stack `0fd277aae` | before the read changes | lease check and startup changes | **all read changes** |
|---|---|---|---|---|
| Point read | 0.340 | 0.322 | 0.299 | **0.215** |
| Full scan, 2,000 rows | 1.866 | 3.368 | 3.366 | **2.406** |
| 1 update : 9 reads | 0.716 | 0.875 | 0.783 | **0.710** |
| Update loop | 3.913 | 2.249 | 2.170 | **2.104** |

Without warm-up (cold), from before the read changes to after them:

| Workload | before | after | pre-stack |
|---|---|---|---|
| Point read | 0.692 | 0.480 | 0.47 |
| Scan | 9.3 | 8.0 | 5.6 |
| Mixed | 1.44 | 1.17 | 0.80 |

**Linux, fingerprint cache alone** (#3009: Ubuntu 24.04, AMD Ryzen 9 3900X, ext4; 1,000-operation or 20-scan warm-up, median of three run means, ms per operation; other workloads were active on the host):

| Workload | .NET 10 before | .NET 10 with cache | .NET 8 before | .NET 8 with cache |
|---|---|---|---|---|
| Point read | 0.440 | 0.331 | 0.453 | 0.354 |
| Scan | 11.14 | 10.12 | 10.54 | 10.18 |
| Mixed | 1.30 | 1.25 | 1.37 | 1.21 |

The point-read gain is supported by disjoint timing ranges and by the removed profile hotspot. The scan ranges overlap, so that change can't be attributed to the cache with confidence. The pre-stack values on that host were 0.213 / 8.34 / 1.31 ms. Windows and Linux numbers must not be combined into one speedup.

## What remains

- **Full scans are about 29% slower than pre-stack in steady state.**
  - About 0.4 ms of the remaining 0.54 ms per scan is creating the lease file. v13's cross-process snapshots need an OS-held lease; pre-stack held the mutex for the whole read instead.
  - The rest is lock contention in the page cache (`Monitor.Enter` in `GetReadablePage`).
- **Cold starts** are dominated by JIT compilation of the larger code. Closing that gap needs ReadyToRun or startup tiering, not storage changes.
- **Incremental engine reopen is not possible without a new coherence protocol.** A persistent engine-state cache validated only by WAL length or the current header fields is unsound: another process can reuse free WAL slots before the cached end without any header change (#3004). The experimental coordinator avoids reopening entirely with its own epoch and lease protocol; see [experimental-coordinator.md](experimental-coordinator.md).

## Tests and measurement tools

- **Read path:** [SharedReadPath_Tests](../LiteDB.Tests/Engine/SharedReadPath_Tests.cs) covers the 100 vs 101 value boundary, the 64 KiB boundary, prefix semantics, a legacy file read first, first-read creation, auto-rebuild under a live reader and the lease lifecycle. Negative controls: the old path fails the single-execution tests, and removing `DeleteOnClose` fails the lease test.
- **Fingerprint cache:** [CollationFingerprintCache_Tests](../LiteDB.Tests/Internals/CollationFingerprintCache_Tests.cs):
  - 108 culture/option combinations (more than the 64 slots), checked concurrently against the uncached algorithm;
  - a warm connection must still reject a checksum-valid altered stamp;
  - encrypted and read-only reopens with index-plan assertions.
- **Measurement:** [tools/SharedReadBenchmarks](../tools/SharedReadBenchmarks/README.md) references an already built production `LiteDB.dll`, so baseline and candidate assemblies stay isolated. The raw Windows benchmark data for #3003 is in [LiteDB-Artifacts](https://github.com/litedb-org/LiteDB-Artifacts/tree/main/pull-requests/3003-shared-mode).
