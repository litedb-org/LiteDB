# Shared read profiling

Compare production assemblies from separate checkouts. The runner references an
already built DLL, so it cannot accidentally rebuild the library with test hooks.
It also rejects a loaded assembly that exposes the engine's test hooks.
It supports the pre-storage-stack `0fd277aae` baseline as well as PR #3003.

```sh
dotnet build /absolute/checkout/LiteDB/LiteDB.csproj -c Release -f net10.0 -p:TestingEnabled=false
dotnet build tools/SharedReadBenchmarks -c Release -f net10.0 \
  -p:LiteDBAssembly=/absolute/checkout/LiteDB/bin/Release/net10.0/LiteDB.dll \
  -o <scratch>/shared-read-baseline
dotnet <scratch>/shared-read-baseline/SharedReadBenchmarks.dll /tmp shared point 20000 10
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
  -o <scratch>/shared-point.nettrace -- \
  dotnet <scratch>/shared-read-baseline/SharedReadBenchmarks.dll /tmp shared point 20000
```

Sampled thread time includes waiting. Inclusive stack percentages overlap and do
not establish the percentage of CPU spent inside an inlined helper. Keep traces
separate from the uninstrumented latency comparison.

See [shared read performance](../../docs/shared-read-performance.md).

`slots <count> <active-slots>` is a separate helper diagnostic. It binds production
slot methods to delegates before timing, fills the specified number of live leases,
then repeatedly releases/replaces the final lease. The complete version set is
validated before and after timing. Counts from 1 through the 65,536-slot limit
show whether selection cost scales with live readers. It reports fill allocation,
fill and close time as well as steady-state latency/CPU/allocation. This bypasses
query execution; do not describe its speedup as application throughput.

For five alternating pairs, including the existing point/scan/mixed workloads:

```sh
python3 scripts/measure-shared-slots.py \
  --baseline <baseline-runner-directory> --candidate <candidate-runner-directory> \
  --scratch <private-directory-on-test-volume> --output <new-results.jsonl>
```

Run once per runtime, serially and without competing tests/builds. Commands,
`TMPDIR`, host, order, timing, failures and JSON results are preserved in an
exclusively created output file; any failed process stops the comparison.

`holder <count>` separately measures production pin acquire/ready/release cycles
with no engine or durable I/O. Reflection overhead is included, and it is a
cost probe rather than a proposed reusable-worker implementation.

`interop <database>` is a line-oriented child protocol for
`scripts/verify-shared-slot-interop.py` (Linux driver). Build the same runner
against each production library, then pass their directories as `--baseline` and
`--candidate` and a private `--scratch`. It alternates writer/reader binary roles,
holds three generations through checkpoint and oldest-first departure, verifies
full payloads and indexed results, rolls back cross-collection deletion, and
checks a transaction witness and final state from a new process. Failed fixture
files remain for investigation. Native kill/recovery and physical integrity
remain the responsibility of the existing Shared/MVCC regression/fuzz suite.

`scripts/measure-shared-contention.py` uses the same production runner directories.
It runs five alternating pairs with two and four separate writer processes. The
owner continuously writes while holding a leased scan; peer arrival intervals
are 5 ms (two writers) and 5/10/15 ms (four). The owner is a saturation workload;
peers report both API latency and delay from their intended arrival, offered work
and unfinished work. Every worker must make progress. A fresh process verifies
all final acknowledged revisions and the full original collection, and final
close must remove WAL content. `--smoke` runs short validation pairs only.

Output includes each participant's mean/p50/p95/p99/worst latency, completed work,
CPU, total allocations, open/close/lifecycle duration, sampled peak WAL bytes and
thread/handle counts, OS peak working set, and post-close memory/thread/handle
counts after 1.2 seconds and a full GC. CPU includes worker initialization and
cleanup; lifecycle wall time includes the start barrier and deliberate idle wait.
The seeded fixture and cold verifier are outside the active-work interval.
Working sets include shared runtime pages and must not be described as unique
physical memory when summed. WAL/handle/thread peaks are samples after operations,
not a guarantee of observing every transient peak. The benchmark does not set
CPU affinity, reset host caches, or alter durability/checkpoint settings.
