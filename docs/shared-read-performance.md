# Shared read performance

After the storage stack (#2998, #2924, #2999, #3000), shared-mode reads were slower than before it (`0fd277aae`). Direct-mode reads barely changed: a point read went from 0.036 to 0.038 ms. The extra cost therefore lies in what shared mode repeats for every operation, not in the query engine or in checksum validation. This document summarizes the investigation, the changes in #3003 and what remains.

Two investigations reached the same diagnosis independently: one on Windows, and one on Linux in the draft #3009. The useful changes from #3009 are part of #3003 now. The workloads use a 2,000-document fixture:
- point read: `FindById`;
- full scan: all 2,000 rows;
- mixed: one update per nine point reads;
- update loop.

## Findings

1. **Engine startup repeated work whose result could not have changed.** A cold point read spent about 68% of its time opening an engine:
   - The page-0 header was read and validated twice: by `DiskService.ValidateExistingData` and again by `LiteEngine.Open`.
   - `ValidateCollationStamp` ran three times per open: before, during and after WAL restore. Each run computed the collation fingerprint, which writes 361 culture comparisons and hashes them with SHA-256. On Linux this fingerprint was 26.2% of the sampled time of a point-read trace. It arrived with the persisted index compatibility changes, so attributing the regression to page checksums alone misses it.
   - With auto-rebuild enabled, every operation also scanned the reader-lease directory (`OldestVersion`).
   - Probing the normally absent rebuild-recovery marker threw a first-chance exception on every open (about 3% of a point read on Linux).
2. **A streaming query ran twice.** `TryBufferResult` executed the query on the writable engine and buffered up to 100 values or 64 KiB. On overflow it discarded that work, registered a lease, opened a second (snapshot) engine with a cold page cache and executed the query again from the start.
3. **Lease registration was a filesystem round trip.** Leases were not deleted on close. The next registration therefore enumerated the `-readers` directory, opened the previous lease exclusively to prove it dead, deleted it and the directory, recreated the directory and created the new lease. An absent directory was detected through a caught `DirectoryNotFoundException`.
4. **Checksum code stayed in the initial JIT tier too long.** Each shared operation runs page-sized CRC loops in a fresh engine, so `Crc32C` ran in tier-0 code with span-helper calls long after the first operations. A fixed warm-up of 1,000 calls was not enough to reach steady state.
5. **Per-document materialization allocated per field.** A steady-state CPU profile on Linux put 28.2% of samples under BSON `ReadDocument` and 18.6% under element decoding (inclusive, overlapping percentages). Every document created a new reader, a new schema callback and new field-name strings.
6. **Smaller costs.** The handle identity check of the Windows handle cache takes about 14% of an open. The mutex holder thread from #3006 adds about 20–30 µs per operation. Each of these safety mechanisms was kept.
7. **Lazy checkpoints move some work to reads and to close.** After a write, up to 50 WAL pages stay in the WAL, and each following operation's fresh engine restores them. On Linux, WAL index restoration appeared in 15.1% of the mixed-workload trace. The final connection close then performs the deferred checkpoint (see "What remains"). A checkpoint on the first read after writes would cost four syncs plus the page copies per update, so it was not added.
8. **Cold runs mostly measure JIT warm-up.** The post-stack code is larger. In short, cold runs it compiled 2.3× more methods in the second half of the run, including `Crc32C`. With the tiered call-counting delay set to 0, a pre-stack scan takes 3.1 ms and the new code 3.5 ms. CRC validation costs about 1.3–1.8 ms per cold scan, but 15 µs per scan in steady state (0.7 µs per page, 11.5 GB/s). The first benchmark published on #3003 used such cold runs and overstated the steady-state gap.

## Changes in #3003

- **The live-reader check for auto-rebuild** runs only when an invalid-state header is about to be rebuilt, still under the mutex. A live reader still blocks the rebuild.
- **Bounded collation fingerprint cache** (from #3009):
  - At most 64 immutable, process-local runtime results, keyed by comparer name, LCID, comparison options and sort version, so equal LCIDs alone do not identify a comparer.
  - A collision recomputes. A runtime that cannot describe its sort tables returns 0 and is asked again on the next request.
  - The cache holds the runtime's answer, never a database's verdict: every loaded data and WAL header is still compared against it, and the persisted fingerprint bytes are unchanged.
- **Header reuse:** the header that `ValidateExistingData` read, recovered and validated is handed to `LiteEngine.Open` once. It is the same engine, still opening, so nothing can write in between; a later open revalidates. New files still use `ReadFull`. #3009 made the same change independently; its `StartupHeader_Tests` cover this implementation.
- **Rebuild marker probe without exceptions** (from #3009): `FileInfo.Attributes` reports an absent marker as `-1` without throwing. Access and I/O errors still throw, and a present marker still blocks opening or creation. `File.Exists` would hide such errors.
- **CRC loops optimized from their first call** (from #3009): `Crc32C.Update` and `UpdatePortable` carry `MethodImplOptions.AggressiveOptimization` on .NET 8+. The polynomial, the checked bytes and the hardware and portable algorithms are unchanged.
- **Query-local reader and field-name reuse** (from #3009):
  - A `DatafileLookup` reuses one resettable `BufferReader` for its query, and a `finally` clears the borrowed page reference after every document, on success or failure.
  - A query-local table of at most 32 short ASCII field names (up to 64 bytes) is verified byte for byte on every hit; Unicode and long names use the ordinary decoder.
  - `DataService` reuses its schema callback.
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

The three series below come from different hosts, runtimes, fixtures and builds. Windows and Linux numbers must not be combined into one speedup, and percentages from different series must not be added.

### Windows

AMD Ryzen 9 9955HX, NVMe, Windows 11, .NET 10.0.11, production assemblies.

**Steady state** (5 s warm-up, then 3 interleaved rounds; median ms per operation):

| Workload | pre-stack `0fd277aae` | before the read changes `f0228d301` | lease check and startup changes | **single execution and leases** `c6d26524e` |
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

**The changes taken over from #3009, measured one at a time** with [tools/SharedReadBenchmarks](../tools/SharedReadBenchmarks/README.md). Each build adds one change to the previous one: `6a8ad1883` (all changes above) → `1286bdbb3` (marker probe) → `4cbbc8227` (CRC loops) → `8c3874b15` (reader and field-name reuse). Median of three run means in ms per operation, with the range; CPU is process CPU time per operation.

| Workload | `6a8ad1883` | + marker probe | + CRC loops | + reader/name reuse |
|---|---|---|---|---|
| Point read, short run | 0.465 (0.457–0.566) | 0.421 | 0.351 | **0.333** (0.309–0.357) |
| Full scan, short run | 9.04 (8.60–9.20) | 8.40 | 7.57 | **7.21** (6.64–12.57) |
| Point read, 5 s warm-up | 0.503 (0.308–0.659) | 0.384 | 0.351 | **0.291** (0.199–0.317) |
| Full scan, 5 s warm-up | 3.22 (2.86–4.36) | 3.75 | 3.56 | **3.10** (2.88–3.49) |
| Mixed, 5 s warm-up | 0.857 (0.757–1.037) | 0.887 | 0.771 | **0.809** (0.778–0.892) |
| Direct scan control, 5 s warm-up | 1.169 | 0.953 | 0.867 | 1.058 |
| Point read CPU ms/op, 5 s warm-up | 0.425 | 0.370 | 0.352 | **0.306** |
| Scan allocation KiB/op | 4,878 | 4,877 | 4,877 | **4,143** |
| Update loop (separate harness, 2,000 updates) | 2.58 | – | – | **2.44** |

The short-run series is consistent: the marker probe, the CRC policy and the reader reuse each lower both point reads and scans, together by about 28% and 20%. The host was noisier during the 5 s series, as the direct-scan control shows: it moved between 0.87 and 1.17 ms without any change to direct reads. So the steady-state scan and mixed differences are within noise, and only the point-read gain and the 15% lower scan allocation are clear.

### Linux (#3009)

Ubuntu 24.04.3, AMD Ryzen 9 3900X, ext4 on LVM, .NET 10.0.11 and 8.0.30, production assemblies. Other database and fuzz workloads were active on the host. Builds: pre-stack `0fd277aae`, #3003 at `137a1d934`, and the #3009 candidate `459e525d1`, runner `81e28693c`. The candidate contains the fingerprint cache, header reuse, marker probe, CRC policy and reader/name reuse, but **not** the single-execution read path, self-deleting leases or the auto-rebuild reader check. Its scans still buffer a prefix, discard it and restart on a second engine.

Median of three run means, ms per operation; "vs" compares the candidate with the named build.

**Short run** (at least 1,000 point operations or 20 scans of warm-up):

| Runtime / workload | pre-stack | #3003 | candidate | vs pre-stack | vs #3003 |
|---|---|---|---|---|---|
| .NET 10 point | 0.2103 | 0.4152 | 0.2227 | +5.9% | −46.4% |
| .NET 10 scan | 8.2688 | 10.6275 | 8.4372 | +2.0% | −20.6% |
| .NET 8 point | 0.2417 | 0.4522 | 0.2380 | −1.5% | −47.4% |
| .NET 8 scan | 8.4257 | 10.7930 | 8.6055 | +2.1% | −20.3% |

**Steady state** (at least 10 s of warm-up):

| Runtime / workload | pre-stack | #3003 | candidate | vs pre-stack | vs #3003 |
|---|---|---|---|---|---|
| .NET 10 point | 0.0977 | 0.1656 | 0.1076 | +10.1% | −35.0% |
| .NET 10 scan | 2.4509 | 2.8061 | 2.4432 | −0.3% | −12.9% |
| .NET 10 mixed | 1.1923 | 1.0191 | 0.9201 | −22.8% | −9.7% |
| .NET 8 point | 0.1200 | 0.2082 | 0.1147 | −4.5% | −44.9% |
| .NET 8 scan | 2.7810 | 3.3013 | 2.8595 | +2.8% | −13.4% |
| .NET 8 mixed | 1.2521 | 1.0538 | 0.9363 | −25.2% | −11.1% |

**Tail latency and resource cost, steady state** (p99 is the median of each run's p99):

| Runtime / workload | pre-stack p99 | candidate p99 | pre-stack KiB/op | candidate KiB/op | pre-stack CPU ms/op | candidate CPU ms/op |
|---|---|---|---|---|---|---|
| .NET 10 point | 0.4128 | 0.4513 | 270.7 | 261.9 | 0.0995 | 0.1433 |
| .NET 10 scan | 5.2955 | 5.4412 | 4,732.4 | 4,408.8 | 2.2725 | 2.4638 |
| .NET 10 mixed | 10.5975 | 5.7110 | 271.4 | 342.1 | 0.1983 | 0.4191 |
| .NET 8 point | 0.5165 | 0.5295 | 270.9 | 262.1 | 0.1230 | 0.1540 |
| .NET 8 scan | 5.6548 | 5.9290 | 4,732.5 | 4,540.1 | 2.6900 | 2.9100 |
| .NET 8 mixed | 10.7118 | 5.9873 | 271.6 | 342.3 | 0.2500 | 0.4400 |

Direct-mode scan controls, #3003 → candidate: .NET 10 1.3906 → 1.2755 ms (allocation 3,764,697 → 3,013,353 bytes per operation), .NET 8 1.7380 → 1.4961 ms.

Final connection close in the mixed workload: pre-stack 0.091 / 0.066 ms, #3003 23.314 / 23.812 ms and candidate 24.085 / 23.188 ms (.NET 10 / .NET 8). Every final run verified the full contents and left no WAL content after close.

Earlier, isolated Linux experiments: the fingerprint cache improved point reads by 22–25%, the CRC policy lowered the short .NET 10 point read from 0.2989 to 0.2255 ms, header reuse by about 6.5% and the marker probe by about another 3%. The reader/name reuse showed an allocation reduction (scans from about 5.30 to 4.51 MB) but no reliable latency gain on its own. These experiments used intermediate revisions and different process lifetimes, so their percentages must not be added.

## What remains

- **Full scans on Windows remain about 29% slower than pre-stack in steady state** (single-execution build).
  - About 0.4 ms of the remaining 0.54 ms per scan is creating the lease file. v13's cross-process snapshots need an OS-held lease; pre-stack held the mutex for the whole read instead.
  - The rest is lock contention in the page cache (`Monitor.Enter` in `GetReadablePage`).
  - On Linux, the #3009 candidate already matches pre-stack scans in steady state even without the single-execution path; the hosts, fixtures and remaining lease costs differ.
- **CPU per operation is higher than pre-stack even where latency matches** (Linux, .NET 10): point reads 0.0995 → 0.1433 ms, mixed 0.198 → 0.419 ms, and mixed allocation +26%. The suspected cause is the #3006 `SharedMutexOwner`: the caller's brief spin and the asynchronously posted release with holder-thread wake-ups. This was not investigated in #3003.
- **Final connection close costs about 23 ms in the mixed workload** (pre-stack: under 0.1 ms), because lazy checkpoints defer the WAL work to close. It happens once per connection, but it is on the closing caller's path.
- **p99 does not improve in every case.** On Linux the candidate's p99 is slightly higher than pre-stack for point reads and scans, while the mixed p99 roughly halves. A mean improvement is not a universal tail-latency win.
- **Cold starts** are dominated by JIT compilation of the larger code. The CRC policy removes one hotspot; closing the rest of the gap needs ReadyToRun or startup tiering, not storage changes.
- **Incremental engine reopen is not possible without a new coherence protocol.** A persistent engine-state cache validated only by WAL length or the current header fields is unsound: another process can reuse free WAL slots before the cached end without any header change (#3004). The experimental coordinator avoids reopening entirely with its own epoch and lease protocol; see [experimental-coordinator.md](experimental-coordinator.md).

## Tests and measurement tools

- **Read path:** [SharedReadPath_Tests](../LiteDB.Tests/Engine/SharedReadPath_Tests.cs) covers the 100 vs 101 value boundary, the 64 KiB boundary, prefix semantics, a legacy file read first, first-read creation, auto-rebuild under a live reader and the lease lifecycle. Negative controls: the old path fails the single-execution tests, and removing `DeleteOnClose` fails the lease test.
- **Fingerprint cache:** [CollationFingerprintCache_Tests](../LiteDB.Tests/Internals/CollationFingerprintCache_Tests.cs):
  - 108 culture/option combinations (more than the 64 slots), checked concurrently against the uncached algorithm;
  - a warm connection must still reject a checksum-valid altered stamp;
  - encrypted and read-only reopens with index-plan assertions.
- **Startup header:** [StartupHeader_Tests](../LiteDB.Tests/Internals/StartupHeader_Tests.cs): an open reads exactly one complete header, also in 17-byte chunks; failed or truncated header reads leave the files untouched and can be retried; a later open revalidates instead of reusing earlier bytes.
- **Marker probe:** [RebuildMarkerProbe_Tests](../LiteDB.Tests/Engine/RebuildMarkerProbe_Tests.cs) covers a subsequently created marker, missing parents, denied access and dangling symlinks on Unix.
- **Reader and name reuse:** [DocumentLookupCursor_Tests](../LiteDB.Tests/Internals/DocumentLookupCursor_Tests.cs) and [BsonFieldNameCache_Tests](../LiteDB.Tests/Internals/BsonFieldNameCache_Tests.cs) cover legacy and schema documents, plain and encrypted multi-page documents, projections, continuation-read failures and retry, retained-buffer collection, collisions and every segment boundary.
- **Measurement:** [tools/SharedReadBenchmarks](../tools/SharedReadBenchmarks/README.md) references an already built production `LiteDB.dll`, so baseline and candidate assemblies stay isolated, and it rejects assemblies with test hooks. Its optional warm-up seconds separate short runs from steady state.
- **Raw data:**
  - Windows benchmark for #3003: [LiteDB-Artifacts `pull-requests/3003-shared-mode`](https://github.com/litedb-org/LiteDB-Artifacts/tree/main/pull-requests/3003-shared-mode).
  - Linux measurements, traces and validation logs from #3009: [LiteDB-Artifacts `pull-requests/3009-shared-read-performance`](https://github.com/litedb-org/LiteDB-Artifacts/tree/7a22a3247351609834da0d84990cd73950012a3e/pull-requests/3009-shared-read-performance).
