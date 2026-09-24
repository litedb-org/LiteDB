# Memory and performance impact per profile

Each profile is compared with the same **pre-PR `dev` baseline**, using the
same workload and host. Values are medians of three fresh processes per
variant/runtime. Parentheses show `(profile / pre-PR dev - 1) * 100`:
**negative means less memory or less time; positive means more memory or a
performance penalty**. There is no single combined performance percentage.

## Results

Managed RAM means retained managed heap after full GC, with the database still
open after the workload; working set includes runtime and native memory.
These overlap and must not be added. Neither is peak transaction memory.
Lookup p99 is the 99th percentile of individual lookup durations, not batch
duration. The hot set has 4,000 documents; scattered lookups cover all 100,000.

### .NET 8.0.30

| Profile | Managed RAM MiB (change) | Working set MiB (change) | Repeated scan time | Hot lookup p99, 16 readers | Scattered lookup p99, 16 readers | Bulk insert time | Index build time |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Pre-PR dev | 133.95 | 209.34 | 183.0 ms | 1029.5 us | 812.9 us | 1231.6 ms | 512.3 ms |
| LowMemory | 9.28 (-93.1%) | 68.55 (-67.3%) | 205.4 ms (+12.2%) | 963.1 us (-6.4%) | 1137.9 us (+40.0%) | 1381.4 ms (+12.2%) | 380.0 ms (-25.8%) |
| Balanced | 66.12 (-50.6%) | 124.07 (-40.7%) | 232.8 ms (+27.2%) | 986.6 us (-4.2%) | 1172.7 us (+44.3%) | 1547.7 ms (+25.7%) | 498.5 ms (-2.7%) |
| Throughput | 131.12 (-2.1%) | 189.99 (-9.2%) | 169.2 ms (-7.6%) | 1054.6 us (+2.4%) | 1193.3 us (+46.8%) | 1308.7 ms (+6.3%) | 469.8 ms (-8.3%) |

| Profile | Hot lookup p50, 1 reader | Hot lookup p99, 1 reader | Hot lookup p50, 16 readers | Hot 16-reader batch | Scattered lookup p99, 16 readers | Index WAL MiB |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Pre-PR dev | 22.2 us | 72.6 us | 56.8 us | 979.6 ms | 812.9 us | 9.5 |
| LowMemory | 20.2 us (-9.0%) | 52.5 us (-27.7%) | 55.7 us (-1.9%) | 913.1 ms (-6.8%) | 1137.9 us (+40.0%) | 12.1 (+27.5%) |
| Balanced | 22.1 us (-0.5%) | 62.4 us (-14.0%) | 54.6 us (-3.9%) | 909.8 ms (-7.1%) | 1172.7 us (+44.3%) | 10.2 (+7.4%) |
| Throughput | 23.5 us (+5.9%) | 111.3 us (+53.3%) | 58.8 us (+3.5%) | 998.8 ms (+2.0%) | 1193.3 us (+46.8%) | 9.6 (+1.6%) |

### .NET 10.0.9

| Profile | Managed RAM MiB (change) | Working set MiB (change) | Repeated scan time | Hot lookup p99, 16 readers | Scattered lookup p99, 16 readers | Bulk insert time | Index build time |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Pre-PR dev | 134.12 | 213.94 | 162.7 ms | 1088.8 us | 886.4 us | 1144.2 ms | 538.9 ms |
| LowMemory | 9.18 (-93.2%) | 75.86 (-64.5%) | 222.3 ms (+36.7%) | 1008.9 us (-7.3%) | 1077.4 us (+21.5%) | 1473.1 ms (+28.8%) | 477.2 ms (-11.5%) |
| Balanced | 66.02 (-50.8%) | 128.33 (-40.0%) | 242.8 ms (+49.2%) | 1055.5 us (-3.1%) | 1181.3 us (+33.3%) | 1706.1 ms (+49.1%) | 486.5 ms (-9.7%) |
| Throughput | 131.02 (-2.3%) | 197.78 (-7.6%) | 167.2 ms (+2.8%) | 961.4 us (-11.7%) | 1098.6 us (+23.9%) | 1393.7 ms (+21.8%) | 457.6 ms (-15.1%) |

| Profile | Hot lookup p50, 1 reader | Hot lookup p99, 1 reader | Hot lookup p50, 16 readers | Hot 16-reader batch | Scattered lookup p99, 16 readers | Index WAL MiB |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Pre-PR dev | 20.7 us | 46.1 us | 53.7 us | 1035.9 ms | 886.4 us | 9.5 |
| LowMemory | 20.3 us (-1.9%) | 35.8 us (-22.3%) | 54.7 us (+1.9%) | 907.9 ms (-12.4%) | 1077.4 us (+21.5%) | 12.1 (+27.5%) |
| Balanced | 21.4 us (+3.4%) | 51.1 us (+10.8%) | 56.0 us (+4.3%) | 936.6 ms (-9.6%) | 1181.3 us (+33.3%) | 10.2 (+7.4%) |
| Throughput | 22.3 us (+7.7%) | 50.4 us (+9.3%) | 55.9 us (+4.1%) | 897.7 ms (-13.3%) | 1098.6 us (+23.9%) | 9.6 (+1.6%) |

## Interpretation

- `LowMemory` reduces retained managed RAM by about 93% and working set by
  65-67%. In these samples, repeated scans cost 12-37% more time, initial bulk
  inserts 12-29%, and scattered lookup p99 22-40%. Hot lookup p99 improves.
- `Balanced` reduces retained managed RAM by about 51% and working set by
  40-41%. Repeated scans cost 27-49% more time, initial bulk inserts 26-49%,
  and scattered lookup p99 33-44%. It does not beat `LowMemory` on these
  measured operations, despite having a larger cache.
- `Throughput` reduces retained managed RAM by only 2% and working set by
  8-9% for this corpus: its 128 MiB cache can retain the database's pages.
  Repeated scans range from 8% faster to 3% slower, while initial bulk inserts
  cost 6-22% more time. Scattered p99 remains 24-47% worse; the .NET 8
  single-reader hot p99 is also 53% worse in this sample matrix.

These are measured workload tradeoffs, not a monotonic ranking of profiles.
All profiles include the PR's engine changes, so their deltas against `dev`
do not isolate the cost of the profile setting itself. Cache size, transaction
safepoints, allocation/GC, JIT and scheduling all influence different operations.
Three local samples cannot establish that a profile is universally fastest.
The tables retain the regressions rather than averaging them into the gains.

For this small hot set, `LowMemory` gives the largest measured memory saving.
Larger reused working sets need their own measurements before choosing a cache
budget. The [profile guide](memory-profiles.md) lists defaults and explicit
overrides. Cache targets are soft and grow on demand; active pages may exceed
them, and transaction thresholds apply per active transaction. Actual data in
an in-memory database is separate from its page cache. No profile is a total
process RAM cap, and no host RAM detection is performed.

## Method and reproduction

Measured 2026-09-12 on Windows 11 build 26200, AMD Ryzen 9 9955HX,
32 logical processors. Both libraries are production Release net8.0 assemblies
with `TESTING` disabled, run explicitly on .NET 8.0.30 and 10.0.9:

- Pre-PR `dev`: `47268cb4b99b1a3d0bd5aed743278ceb45a19b49`;
  SHA-256 `1B6D2E2012BC2F1337339D64BE078D37226F37DB12A046D308B17A7DC1C7736A`.
- Current profile implementation: production code in `4586c1d2`;
  SHA-256 `6D44F83FC5AD1D48CD24D7FA7D45ECD98FA8FF3A02731ED4ABAD5ABD808381CF`.
  This is the same current library used in the preceding PR comparison.

The [runner](../tools/MemoryProfiles/README.md) inserts 100,000 documents with
900-character payloads into a new file database, measures scans and lookups,
then creates an integer index with automatic checkpointing disabled to measure
index WAL size. Each reader performs 10,000 measured lookups after warming its
working set. The initial bulk insert is the first timed operation and includes
JIT/startup effects; it is not a warmed steady-state write benchmark. All runs
produced the same 126,967,808-byte database (121.09 MiB).

Run each process sequentially. For each runtime, use three rounds in this order:
pre-PR dev, LowMemory, Balanced, Throughput; reverse that order in round two.
The OS file cache is not flushed. Keep builds and tests separate from timing.
All 24 raw reports are in
[memory-measurements/profile-impact](memory-measurements/profile-impact/).
Each table cell is calculated from its metric's three process values; each
percentage compares those medians before rounding. Sample variation is retained
in the raw reports; these are local acceptance measurements, not confidence
intervals or portable performance guarantees.

The pre-PR library has no profile or bounded-cache setting. Its runner argument
is named `Balanced` only for compatibility: the runner leaves that library's
original defaults unchanged. A null transaction threshold means the old library
does not expose that diagnostic. Every run verified document counts/IDs and
indexed enumeration. All runs ended with zero writable/in-use pages; current
profiles also verified zero lost, pinned and loading frames and retained page
allocation within the rounded cache target.

## Other comparisons

- [Original memory improvements](memory-management-benchmark-results.md):
  earlier PR revision, 120k-document corpus, Linux host, including encrypted
  inserts, vector indexes and compiled-expression retention. Those historical
  numbers should not be combined with this Windows profile table.
- [Latency optimization versus the preceding PR revision](memory-profile-results.md):
  compares against the already memory-bounded `6debf0f4`, not pre-PR `dev`.
  Its percentages answer a different question and come from a separate run set.
