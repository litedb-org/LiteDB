# Query frontend and end-to-end benchmarks

This dependency-free .NET 8 harness runs unchanged against the baseline and new
production assemblies. It measures translation, construction, fully consumed
indexed queries, changing parameters, a full scan, and a SQL control. When the
assembly supports `BsonExpression.Bind`, it also measures reusable bindings.

The database contains 10,000 deterministic documents in memory, with indexes on
`_id`, `Age`, and `City`. Setup, inserts, index construction, and one workload-sized
warmup are outside timing. Each case reports 15 batches of elapsed nanoseconds,
thread allocations, Gen0 collections per 1,000 operations, and a consumed-result
checksum. Sample counts and operation counts are included in the raw JSON.

Build the library at each revision with `TestingEnabled=false`, putting each
assembly in a separate output directory. For example, in the baseline checkout:

```sh
dotnet build LiteDB/LiteDB.csproj -c Release -f net8.0 \
  -p:TestingEnabled=false -o /tmp/query-ir-before-library
```

In the implementation checkout, compile the same harness against that assembly:

```sh
dotnet build tools/QueryIrBenchmarks -c Release \
  -p:LiteDBAssembly=/tmp/query-ir-before-library/LiteDB.dll \
  -o /tmp/query-ir-before
DOTNET_TieredCompilation=0 dotnet /tmp/query-ir-before/QueryIrBenchmarks.dll before > before.json
```

Repeat with the implementation assembly and separate output paths. Run the two
executables sequentially, alternating order across multiple process pairs. Avoid
concurrent builds/tests or other CPU-intensive work. On Linux, optionally prefix
both commands with `taskset -c N` using the same available CPU. Disabling tiered
compilation avoids measuring different JIT promotion stages between revisions;
it is an explicit measurement setting, not a recommended application setting.

```sh
python3 tools/QueryIrBenchmarks/compare.py \
  --before before-1.json before-2.json before-3.json \
  --after after-1.json after-2.json after-3.json
```

The comparison checks all shared workloads' result checksums and reports medians
across their samples. Inspect the individual samples for variance. Small timing
differences on a shared machine should not be treated as reliable speedups.
