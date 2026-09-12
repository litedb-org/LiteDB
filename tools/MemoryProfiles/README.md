# Profile and cache-latency measurements

This runner compares production libraries using the same 100,000-document
workload. It references a chosen `LiteDB.dll`, so the preceding implementation
can run unchanged with the `Balanced` profile. The report includes the assembly
SHA-256 and runtime. Build the library with `TESTING` disabled.

```powershell
dotnet build LiteDB/LiteDB.csproj -c Release -f net8.0 -p:TestingEnabled=false
dotnet build tools/MemoryProfiles/MemoryProfiles.csproj -c Release -o artifacts_temp/profiles
dotnet exec --fx-version 8.0.30 artifacts_temp/profiles/MemoryProfiles.dll current Balanced balanced.json
dotnet exec --fx-version 8.0.30 artifacts_temp/profiles/MemoryProfiles.dll current LowMemory low-memory.json
dotnet exec --fx-version 8.0.30 artifacts_temp/profiles/MemoryProfiles.dll current Throughput throughput.json
```

Select an installed runtime for `--fx-version`; the same executable can also
run on .NET 10. To compare a different library, pass its absolute path using
`-p:LiteDBAssembly=...` when building the runner into a separate output directory.
The optional final arguments override cache MiB and transaction pages:

```powershell
dotnet exec --fx-version 8.0.30 artifacts_temp/profiles/MemoryProfiles.dll current Throughput throughput-64.json 64 4000
```

Run one process at a time without simultaneous builds/tests. Repeat each case
at least three times and alternate their order. Report medians and preserve
the individual results. Do not include tracing runs in timing summaries.

The workload inserts documents containing a 900-character payload, checkpoints,
and measures first and repeated full scans. It then measures 10,000 lookups per
dedicated worker at 1/4/16 threads against a warmed 4,000-document hot set, which
fits even in the LowMemory cache. A separate 16-thread scattered lookup case
uses the full corpus to exercise cache misses. Individual p50/p99 latency and
batch throughput are distinct measurements.

Finally, it creates an integer index with checkpoint disabled, records WAL
length, checkpoints, verifies indexed enumeration, and checks zero outstanding
pins, loading/writable pages, lost frames, and allocation within the rounded
target. Managed memory is sampled after full GC with the database still open.
Working set includes runtime and native memory. Neither number measures peak
memory during a transaction.

`Monitor.LockContentionCount` is a process-wide count, not a cache-specific
wait-duration metric. Sampled stack traces can identify the contended paths;
use them to interpret results, separately from the uninstrumented timings.
The OS file cache is not flushed. These are local acceptance measurements,
not statistically rigorous or portable performance guarantees.
