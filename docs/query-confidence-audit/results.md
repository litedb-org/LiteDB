# Fresh cumulative measurements, 2026-09-18

Before: current `dev` at `7d2a16c4`. After: its temporary merge with PR head
`2a2c74e9` plus the two scanner boundary fixes in `21481eeea`. The subsequent
contradiction fallback was validated separately in `final-after.json`, with all
16 result checksums unchanged; its unpaired timings are not folded into these tables.
All 16 workload checksums match across all four processes in each affinity group.
The table uses the median of two process medians; each process contains nine batches.
Allocation change uses the first before/after process medians.

### One CPU, default tiering

| Complete query | Before (µs) | After (µs) | Time change | Allocation change |
|---|---:|---:|---:|---:|
| ordinary-id | 131.69 | 61.19 | -53.5% | -39.6% |
| ordinary-combined | 188.16 | 85.39 | -54.6% | -47.3% |
| ordinary-projection | 238.88 | 144.28 | -39.6% | -35.5% |
| sql-id | 101.87 | 48.40 | -52.5% | -41.4% |
| or-linq | 22844.21 | 127.48 | -99.4% | -99.9% |
| range-linq | 10993.99 | 172.50 | -98.4% | -99.8% |
| contradiction-linq | 10965.13 | 45.46 | -99.6% | -100.0% |
| guard-linq | 21493.37 | 81.24 | -99.6% | -99.9% |
| contains-range-linq | 437905.91 | 1061.60 | -99.8% | -99.2% |
| common-or-linq | 24661.53 | 342.99 | -98.6% | -99.8% |
| secondary-range-count | 12500.89 | 6118.16 | -51.1% | -77.3% |
| primary-full-count | 10777.69 | 10530.11 | -2.3% | -62.8% |
| exists-linq | 36.29 | 66.14 | +82.3% | -52.4% |
| topn-single | 56481.86 | 80901.94 | +43.2% | -14.9% |
| topn-mixed | 99750.65 | 103762.61 | +4.0% | -16.7% |
| scan-control | 46542.86 | 99933.56 | +114.7% | -27.3% |

### Four CPUs, default tiering

| Complete query | Before (µs) | After (µs) | Time change | Allocation change |
|---|---:|---:|---:|---:|
| ordinary-id | 65.47 | 45.37 | -30.7% | -39.4% |
| ordinary-combined | 48.47 | 27.49 | -43.3% | -47.3% |
| ordinary-projection | 67.33 | 43.90 | -34.8% | -35.3% |
| sql-id | 22.22 | 11.91 | -46.4% | -41.4% |
| or-linq | 21082.28 | 25.13 | -99.9% | -99.9% |
| range-linq | 10348.37 | 33.88 | -99.7% | -99.8% |
| contradiction-linq | 10243.30 | 10.24 | -99.9% | -100.0% |
| guard-linq | 22518.75 | 16.37 | -99.9% | -99.9% |
| contains-range-linq | 378422.69 | 397.28 | -99.9% | -99.2% |
| common-or-linq | 24476.10 | 86.30 | -99.6% | -99.8% |
| secondary-range-count | 10573.76 | 2508.98 | -76.3% | -77.3% |
| primary-full-count | 7832.85 | 2838.65 | -63.8% | -62.8% |
| exists-linq | 35.76 | 12.81 | -64.2% | -52.3% |
| topn-single | 52312.78 | 24016.77 | -54.1% | -14.9% |
| topn-mixed | 73454.36 | 27258.56 | -62.9% | -16.7% |
| scan-control | 45328.36 | 38212.88 | -15.7% | -27.3% |
