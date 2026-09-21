# Issue #2920 measurements

Measured 2026-09-19 on Ubuntu 24.04/ext4, AMD Ryzen 9 3900X (12 cores/24 threads), .NET 8.0.30, SDK 10.0.400.

Release library builds use `TestingEnabled=false`. Timed runs are serial, use `DOTNET_TieredCompilation=0`, one discarded warmup and five measured trials per workload, with 5,000 pre-generated documents. Tables show medians in milliseconds; smaller is better. Each trial inserts all documents, updates half, reads 1,000 IDs, scans all documents, and rebuilds through the real file path. CHECKPOINT=0 isolates WAL bytes before explicit checkpoints. Data, WAL, indexes and schema pages are included in file sizes. The mixed workload inserts BSON, reopens with compact writes, and updates half; its initial size therefore remains legacy-sized.

Baseline is upstream dev `f0afcc13ffdab1aacf5f9d2b69824a88e225646a`; final is `0151ccc3917fd09dc08d7471225d9bca2393547c`. The [assembly provenance manifest](assemblies.json) preserves every measured SHA-256. Only baseline and final have retained immutable source revisions and are rebuildable. The intermediate DLLs and their exact uncommitted source snapshots were not retained, so those rows are historical development observations rather than reproducible evidence. `pilot/` retains the initial tiered-JIT runs; those runs motivated the controlled rerun and are not used in these tables. This is a warm OS-cache local microbenchmark, not a cold-I/O or multi-process scaling claim. The host was not CPU-isolated; small timing differences are noise. Allocations count the measuring thread, not process peak memory. Catalog cache bytes are conservative accounted retained bytes after reads, excluding active snapshots and bounded admission hints.

## Every implementation increment

| Stage | Stable insert | Stable point | Stable scan | Array insert | Array scan | Dynamic insert | Tiny insert | Large insert |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| baseline | 45.98 | 28.19 | 15.30 | 117.38 | 67.75 | 45.72 | 29.87 | 182.32 |
| boundary | 46.27 | 29.40 | 15.91 | 110.95 | 68.14 | 43.16 | 30.75 | 183.91 |
| compact-v1 | 50.22 | 35.17 | 12.83 | 88.88 | 34.95 | 74.68 | 42.31 | 216.17 |
| schema-cache | 49.22 | 28.50 | 12.42 | 88.48 | 35.62 | 68.94 | 41.05 | 215.82 |
| admission | 48.63 | 28.73 | 12.49 | 88.57 | 34.20 | 51.64 | 30.50 | 219.56 |
| fallback | 49.59 | 28.58 | 12.81 | 91.48 | 36.35 | 51.88 | 29.45 | 192.95 |
| final | 49.61 | 28.12 | 12.32 | 90.01 | 37.70 | 44.32 | 29.69 | 189.98 |
| final-legacy | 43.67 | 29.35 | 16.02 | 113.53 | 70.32 | 48.21 | 29.45 | 189.34 |

Stages: `boundary` introduces the BSON-only storage boundary; `compact-v1` adds opt-in codec/catalog/promotion/rebuild; `schema-cache` adds the bounded committed-reader cache; `admission` avoids encoding unseen scalar shapes, bypasses tiny documents and retains candidate hints across transactions; `fallback` adds dynamic backoff and the large-scalar saving threshold; `final` requires distinct document identities for schema admission so dynamic updates create no catalog; `final-legacy` measures the resulting implementation with compact writes disabled.

## Final storage and WAL

| Workload | BSON bytes | Compact bytes | Saving | BSON insert WAL | Compact insert WAL | BSON update WAL | Compact update WAL | Schema pages (bytes) |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| stable | 2,547,712 | 1,089,536 | 57.2% | 2,555,904 | 1,097,728 | 2,277,376 | 819,200 | 8,192 |
| optional | 1,851,392 | 925,696 | 50.0% | 1,859,584 | 933,888 | 1,581,056 | 655,360 | 8,192 |
| nested | 3,203,072 | 1,490,944 | 53.5% | 3,211,264 | 1,499,136 | 2,932,736 | 1,220,608 | 8,192 |
| arrays | 7,102,464 | 4,382,720 | 38.3% | 7,110,656 | 4,390,912 | 6,832,128 | 4,104,192 | 8,192 |
| dynamic | 2,121,728 | 2,121,728 | 0.0% | 2,129,920 | 2,129,920 | 1,851,392 | 1,851,392 | 0 |
| types | 2,834,432 | 1,449,984 | 48.8% | 2,842,624 | 1,458,176 | 2,564,096 | 1,179,648 | 8,192 |
| tiny | 499,712 | 499,712 | 0.0% | 507,904 | 507,904 | 229,376 | 229,376 | 0 |
| large | 82,493,440 | 82,493,440 | 0.0% | 82,501,632 | 82,501,632 | 41,263,104 | 41,263,104 | 0 |
| mixed | 2,547,712 | 2,547,712 | 0.0% | 2,555,904 | 2,555,904 | 2,277,376 | 2,301,952 | 8,192 |

## Final operation timings (baseline → compact)

| Workload | Insert | Update | 1,000 point reads | Full scan | Rebuild |
| --- | ---: | ---: | ---: | ---: | ---: |
| stable | 45.98 → 49.61 | 26.43 → 25.69 | 28.19 → 28.12 | 15.30 → 12.32 | 81.15 → 74.06 |
| optional | 39.21 → 46.74 | 23.01 → 22.48 | 28.08 → 27.58 | 12.63 → 10.77 | 67.74 → 74.32 |
| nested | 49.80 → 58.29 | 33.71 → 31.64 | 32.88 → 29.46 | 19.68 → 16.11 | 94.23 → 86.70 |
| arrays | 117.38 → 90.01 | 61.56 → 51.28 | 40.60 → 33.18 | 67.75 → 37.70 | 216.81 → 148.51 |
| dynamic | 45.72 → 44.32 | 26.29 → 25.31 | 28.19 → 27.20 | 15.14 → 15.13 | 72.13 → 76.27 |
| types | 50.80 → 51.34 | 25.38 → 24.41 | 29.66 → 28.02 | 16.81 → 14.94 | 77.52 → 76.70 |
| tiny | 29.87 → 29.69 | 16.61 → 16.55 | 25.57 → 25.82 | 4.59 → 4.88 | 46.06 → 50.49 |
| large | 182.32 → 189.98 | 88.33 → 90.72 | 39.91 → 40.55 | 59.35 → 59.67 | 427.59 → 433.11 |
| mixed | 41.71 → 43.77 | 25.29 → 34.10 | 28.01 → 29.28 | 15.00 → 14.91 | 71.70 → 82.32 |

## Allocations and catalog cache

| Workload | Insert allocated bytes (BSON → compact) | Scan allocated bytes (BSON → compact) | Accounted cache bytes |
| --- | ---: | ---: | ---: |
| stable | 56,940,328 → 61,303,832 | 18,577,120 → 16,606,432 | 2,140 |
| optional | 50,152,584 → 55,463,680 | 15,871,736 → 15,131,376 | 7,628 |
| nested | 56,780,216 → 65,824,216 | 21,876,896 → 19,979,512 | 2,704 |
| arrays | 63,170,776 → 78,562,352 | 73,683,800 → 58,371,968 | 2,260 |
| dynamic | 51,876,856 → 53,243,792 | 17,881,368 → 18,201,464 | 0 |
| types | 56,476,280 → 63,457,120 | 19,186,232 → 18,677,488 | 2,140 |
| tiny | 49,923,120 → 50,123,152 | 6,625,944 → 6,945,976 | 0 |
| large | 205,792,288 → 206,397,312 | 184,409,048 → 184,729,080 | 0 |
| mixed | 55,104,424 → 55,304,456 | 18,578,624 → 17,779,168 | 2,140 |

## Larger rebuild

100,000 stable documents, one warmup and three measured trials:

| Stage | File bytes | Pages | Rebuild ms | Rebuilt bytes |
| --- | ---: | ---: | ---: | ---: |
| baseline-large | 50,487,296 | 6,163 | 1503.89 | 50,487,296 |
| final-large | 21,045,248 | 2,569 | 1394.90 | 21,045,248 |

## Interpretation and reproduction

Stable, optional, nested and type-changing documents save roughly 49–57% of initial file bytes; arrays save 38%. Dynamic dictionaries, tiny documents and large scalar payloads keep BSON and allocate no schema pages. Compact stable/optional/nested inserts still cost more CPU, and converting existing BSON during mixed-file updates costs more than writing BSON again. Array scans and inserts are faster. These measurements compare explicit compact and legacy modes and predate the `Auto` write policy.

```sh
dotnet build LiteDB/LiteDB.csproj -c Release -f net8.0 -p:TestingEnabled=false
dotnet build tools/CompactStorage -c Release
DOTNET_TieredCompilation=0 dotnet tools/CompactStorage/bin/Release/net8.0/CompactStorage.dll trial compact 5000 5
DOTNET_TieredCompilation=0 dotnet tools/CompactStorage/bin/Release/net8.0/CompactStorage.dll large compact 100000 3 stable
python3 scripts/summarize-compact-benchmarks.py
```

Pass `legacy` instead of `compact` for legacy writes. `-p:LiteDBAssembly=/absolute/path/LiteDB.dll` builds the same harness against another assembly; use the provenance manifest's source revisions for reproducible comparisons and separate output directories. Raw JSONL includes every measured trial, page counts, catalog bytes, insert/update WAL, all timed operation allocations, and rebuild size.
