# Memory profile and latency results

Measured 2026-09-12 on Windows 11 build 26200, AMD Ryzen 9 9955HX,
32 logical processors. Compare the merged PR revision `6debf0f4` (`before`)
with the shared-reader and profile changes (`after`). This is a comparison
against the preceding PR revision, not a claim of parity with upstream `dev`.

## Method

The [runner](../tools/MemoryProfiles/README.md) uses 100,000 documents with
900-character payloads. The 4,000-document hot lookup set fits in all three
profiles. A separate scattered lookup workload addresses the entire corpus.
All runs use production Release libraries with `TESTING` disabled. Three fresh
processes per variant/runtime run sequentially, with variant order reversed
in the middle round. Tables report medians of the three processes; raw samples
are in [memory-measurements/profiles](memory-measurements/profiles/).

The same net8.0 production assembly runs on .NET 8.0.30 and 10.0.9. SHA-256:

- Before: `C5EF5592EA83755DB0E50849DC82A6468EAD817F69E82604567DC3BAD65695ED`
- After: `6D44F83FC5AD1D48CD24D7FA7D45ECD98FA8FF3A02731ED4ABAD5ABD808381CF`

Each reader records 10,000 individual lookup samples. Managed memory is
sampled after full GC with the database still open; it is retained memory,
not peak memory. The OS file cache is warm. These local acceptance samples
are not a statistical performance guarantee. CPU scheduling, JIT, GC, and
randomized expression hashes can affect individual processes.

## Reader latency and memory

| Runtime | Variant | Retained managed MiB | Cache MiB | 1-reader p50 / p99 us | 16-reader p50 / p99 us | 16-reader batch ms | Scattered 16-reader batch ms |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 8 | Before, Balanced | 66.14 | 64.06 | 20.9 / 42.3 | 75.9 / 1041.2 | 1396.5 | 1409.0 |
| 8 | After, Balanced | 66.11 | 64.06 | 20.6 / 74.4 | 45.8 / 822.3 | 755.7 | 1031.5 |
| 8 | After, LowMemory | 9.27 | 8.06 | 18.7 / 36.8 | 46.3 / 730.0 | 715.9 | 1137.4 |
| 8 | After, Throughput | 131.12 | 128.06 | 20.1 / 31.6 | 45.3 / 733.7 | 709.3 | 876.8 |
| 10 | Before, Balanced | 66.06 | 64.06 | 19.4 / 29.9 | 63.7 / 1013.8 | 1321.4 | 1464.1 |
| 10 | After, Balanced | 66.02 | 64.06 | 19.2 / 62.7 | 45.0 / 811.6 | 743.7 | 894.4 |
| 10 | After, LowMemory | 9.18 | 8.06 | 18.3 / 28.8 | 43.3 / 743.6 | 696.5 | 952.9 |
| 10 | After, Throughput | 131.02 | 128.06 | 19.8 / 29.1 | 45.7 / 748.0 | 735.9 | 888.2 |

At the unchanged Balanced budget, the hot 16-reader batch takes 45.9% less
time on .NET 8 and 43.7% less on .NET 10. Individual lookup p50 improves
39.7% / 29.4%, and p99 improves 21.0% / 19.9%. Scattered-reader batch time
improves 26.8% / 38.9%. Process-wide monitor contentions in the hot batch fall
from 45,825 to 6,017 and from 61,700 to 5,273 respectively.

LowMemory retains about 86% less managed memory than Balanced in this workload
while retaining its hot-reader benefit. It does more I/O in the scattered case.
This is useful when the frequently reused set fits in 8 MiB; larger hot sets
need separate measurements. Throughput roughly doubles retained cache memory
and helps the larger working set; it is not needed to obtain the concurrent
hot-reader improvement.

Single-reader p50 is essentially unchanged with Balanced. Its measured p99 is
**worse** in this sample matrix, so this change does not establish uniformly
better tail latency. A longer steady-state single-reader benchmark is still
needed to separate implementation effects from JIT/GC and scheduling.

## Scans and writes

| Runtime | Variant | Repeated scan ms | Insert 100k ms | Index build ms | Index WAL MiB |
| --- | --- | ---: | ---: | ---: | ---: |
| 8 | Before, Balanced | 185.2 | 1336.1 | 341.7 | 10.19 |
| 8 | After, Balanced | 179.9 | 1342.5 | 350.9 | 10.19 |
| 8 | After, LowMemory | 178.3 | 1232.5 | 342.6 | 12.09 |
| 8 | After, Throughput | 148.6 | 1200.3 | 351.9 | 9.64 |
| 10 | Before, Balanced | 186.5 | 1198.5 | 338.4 | 10.19 |
| 10 | After, Balanced | 180.0 | 1343.4 | 331.4 | 10.19 |
| 10 | After, LowMemory | 179.8 | 1136.4 | 370.8 | 12.09 |
| 10 | After, Throughput | 139.5 | 1165.6 | 323.7 | 9.64 |

Balanced scan and index differences are small. The .NET 10 insert sample is
12.1% slower, and the .NET 8 index sample is 2.7% slower; there is no blanket
write-performance improvement claim. Inserts are the first timed operation,
so their timings include warmup effects. Throughput improves repeated scans
by about 17% / 23% relative to the new Balanced profile in this corpus.

LowMemory's smaller transaction threshold increases index WAL size by 18.7%
relative to Balanced. Throughput reduces it by 5.4%, at the cost of a higher
per-transaction page threshold. Cache targets and transaction thresholds can
be overridden independently; the [profile guide](memory-profiles.md) explains
their transient-memory implications.

## Implementation and validation

- A fixed 4,096-reference hint table allows atomic sharing of pages that are
  already pinned. It adds 32 KiB on a 64-bit host and introduces no locks.
  First-pin and final-release transitions still use the existing monitor.
  Identity is checked after acquiring a pin to handle eviction/reuse races.
- Hints are removed before frames become reusable and cleared on disposal,
  so they cannot retain released segment arrays. Unused per-frame timestamps
  are removed. The measured retained Balanced heap is slightly smaller.
- Writable copies run outside the monitor with a source pin. New writable
  pages are cleared outside it; full copies avoid redundant clearing.
- The compiled-expression cache retains its 1,000-entry bound and uses four
  entries per bucket so colliding hot expressions and different delegate
  types can coexist. It remains atomically published without a monitor.
- Before tracing showed cache read/release methods dominating sampled thread
  time. Those traces are diagnostic evidence, not CPU-cycle attribution, and
  tracing runs are excluded from the timing tables.
- Release solution build passes. .NET 8/10 each pass 497 tests (7 skipped);
  .NET Framework 4.6.1/4.8.1 each pass 496 (8 skipped). The 44-test cache,
  sharing, copy-failure, and expression suite passes five consecutive runs.
- All 24 measured workloads validate document IDs/counts, indexed enumeration,
  zero outstanding pins/writable/loading pages, zero lost frames, and retained
  cache allocation within the rounded target.

The earlier [Linux impact table](memory-management-benchmark-results.md) and
[correction measurements](memory-management-validation.md) describe other
revisions and workloads. Their percentages should not be combined with these.
