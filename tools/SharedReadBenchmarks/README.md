# Shared read profiling

Compare production assemblies from separate checkouts. The runner references an
already built DLL, so it cannot accidentally rebuild the library with test hooks.
It also rejects a loaded assembly that exposes the engine's test hooks.
It supports the pre-storage-stack `0fd277aae` baseline as well as PR #3003.

```sh
dotnet build /absolute/checkout/LiteDB/LiteDB.csproj -c Release -f net10.0 -p:TestingEnabled=false
dotnet build tools/SharedReadBenchmarks -c Release -f net10.0 \
  -p:LiteDBAssembly=/absolute/checkout/LiteDB/bin/Release/net10.0/LiteDB.dll \
  -o artifacts_temp/shared-read-baseline
dotnet artifacts_temp/shared-read-baseline/SharedReadBenchmarks.dll /tmp shared point 20000 10
```

Repeat the build into a different output directory for each candidate. Alternate
baseline/candidate order for at least three rounds, with a fresh process each time.
Use `-f net8.0` and a net8.0 library to measure .NET 8 separately. Do not run tests
or competing builds during timing runs. Record other host workloads and do not
combine Windows and Linux measurements into one speedup.

Arguments are scratch parent, `shared|direct`, `point|scan|mixed|phases`, measured
operation count, and optional minimum warmup seconds (default zero). Each invocation creates and removes its own uniquely named child
directory. The fixture is 2,000 documents with integer IDs, an integer value and a
200-character payload. Durable writes retain their default setting.

- `point`: indexed `FindById` with complete document checks.
- `scan`: consume and verify all IDs, values and payloads in order.
- `mixed`: one update per nine point reads; verify the final database against an
  independent array of expected values, outside the measurement interval.
- `phases`: consume the same scan through `ILiteEngine.Query`, separating the time
  to obtain the reader, iterate it and dispose it.

Warmup always runs at least 1,000 point/mixed operations or 20 scans. Supply `10`
seconds to allow tiered compilation to settle before measuring steady state; the
count-only default measures an earlier stage of the process lifetime. Run both
when investigating startup regressions. A few thousand calls can still execute
instrumented tier-0 code and should not be labeled steady state.

JSON output includes the first operation, actual warmup count/duration, mean/p50/p99 latency,
process CPU time, process-wide allocated bytes and the library SHA-256. The first
operation excludes fixture creation and may benefit from code/global state warmed
by seeding; it is not a measurement of cold process startup. Allocation is not live
memory or working set. Payload checking contributes to the measured time equally
for all builds. `phases` adds clock reads and uses a different API, so compare its
builds directly rather than substituting it for `scan`. Ten consecutive window
means retain time order so JIT transitions or changes in host load remain visible.
Final connection-close time is measured separately; output includes data-file size
and WAL bytes before/after close, and the runner rejects retained WAL content after
all readers and connections have closed. This is end-state size, not peak WAL size.

For a separate diagnostic run, use an installed `dotnet-trace`:

```sh
dotnet-trace collect --profile dotnet-sampled-thread-time --format Speedscope \
  -o artifacts_temp/shared-point.nettrace -- \
  dotnet artifacts_temp/shared-read-baseline/SharedReadBenchmarks.dll /tmp shared point 20000
```

Sampled thread time includes waiting. Inclusive stack percentages overlap and do
not establish the percentage of CPU spent inside an inlined helper. Keep traces
separate from the uninstrumented latency comparison.

See [the PR #3003 investigation](../../docs/shared-read-performance.md).
