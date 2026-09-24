# Memory-management acceptance measurements

This runner complements BenchmarkDotNet with individual-operation latency samples
and WAL/cache diagnostics. It runs the same public-API workload against a chosen
Release `LiteDB.dll`, including the engine before the memory feature existed.
Build and run one profile at a time; simultaneous runs distort contention results.

```powershell
dotnet build LiteDB/LiteDB.csproj -c Release -f net8.0 -p:TestingEnabled=false
dotnet build tools/MemoryValidation/MemoryValidation.csproj -c Release -o artifacts_temp/memory-current
dotnet exec --fx-version 8.0.30 artifacts_temp/memory-current/MemoryValidation.dll current 64 200000 results.json
```

Choose an installed runtime for `--fx-version`; the net8.0 executable also runs
on .NET 10 with an explicit .NET 10 version. To compare another checkout, build
its library first, then pass `-p:LiteDBAssembly=<absolute-path-to-LiteDB.dll>` when
building this runner into a separate output directory. The report records the
runtime, assembly path and SHA-256. Neither library should define `TESTING`.

Run the 200,000-document workload at 8, 64, and 256 MiB on each runtime. Run
900,000 documents at 64 MiB for the larger index-build comparison. The baseline
has no cache-size setting: its reported profile is only a label, and its original
extensible transaction budget remains in effect.

Measurements include:

- First and second scans after checkpoint, with 900-byte payloads. “Cold” means
  the engine cache after checkpoint, **not** a flushed OS filesystem cache.
- 2,000 lookup samples per dedicated worker at 1/4/16 threads, including p50/p99
  and CLOCK frames examined per lookup and per cache miss. Unsupported baseline
  CLOCK counters are zero; the per-miss ratio is null when there are no misses.
- 100 individual rollback/release samples after 12,000-document reads.
- A separate 8 MiB cache/8,192-page transaction-limit pressure workload: 100
  releases after 40,000-document reads. The bounded engine must release excess
  segments, return below the rounded target, and retain no readable pins.
- Index construction with checkpoint disabled, wall time and final WAL bytes,
  followed by checkpoint and an indexed count check.
- 10,000 inserts, 20,000 encrypted updates, and 100 vector searches over a
  separate 1,000-vector collection (the 200k workload only).

These are local acceptance measurements, not statistically rigorous performance
claims. p99 from 100 release samples is particularly noisy. Re-run on the target
hardware and use BenchmarkDotNet for throughput comparisons before tuning
application-specific defaults. Each run owns and deletes one GUID temp directory.
