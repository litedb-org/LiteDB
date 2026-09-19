#!/usr/bin/env python3
"""Render measured medians; retain raw JSONL so allocations and variance are auditable."""
import json
import pathlib
import statistics

root = pathlib.Path(__file__).resolve().parent.parent / "docs" / "benchmarks" / "2920"
stages = ["baseline", "boundary", "compact-v1", "schema-cache", "admission", "fallback", "final", "final-legacy"]
shapes = ["stable", "optional", "nested", "arrays", "dynamic", "types", "tiny", "large", "mixed"]
data = {name: [json.loads(line) for line in (root / (name + ".jsonl")).read_text().splitlines()] for name in stages}


def median(stage, shape, metric, submetric=None):
    values = [row[metric][submetric] if submetric else row[metric] for row in data[stage] if row["shape"] == shape]
    return statistics.median(values)


lines = ["# Issue #2920 measurements", "", "Measured 2026-09-19 on Ubuntu 24.04/ext4, AMD Ryzen 9 3900X (12 cores/24 threads), .NET 8.0.30, SDK 10.0.400.", "",
    "Release library builds use `TestingEnabled=false`. Timed runs are serial, use `DOTNET_TieredCompilation=0`, one discarded warmup and five measured trials per workload, with 5,000 pre-generated documents. Tables show medians in milliseconds; smaller is better. Each trial inserts all documents, updates half, reads 1,000 IDs, scans all documents, and rebuilds through the real file path. CHECKPOINT=0 isolates WAL bytes before explicit checkpoints. Data, WAL, indexes and schema pages are included in file sizes. The mixed workload inserts BSON, reopens with compact writes, and updates half; its initial size therefore remains legacy-sized.", "",
    "Baseline is upstream dev `f0afcc13ffdab1aacf5f9d2b69824a88e225646a`. Each intermediate production assembly was saved before the next change. `pilot/` retains the initial tiered-JIT runs; those runs motivated the controlled rerun and are not used in these tables. This is a warm OS-cache local microbenchmark, not a cold-I/O or multi-process scaling claim. The host was not CPU-isolated; small timing differences are noise. Allocations count the measuring thread, not process peak memory. Catalog cache bytes are conservative accounted retained bytes after reads, excluding active snapshots and bounded admission hints.", "",
    "## Every implementation increment", "",
    "| Stage | Stable insert | Stable point | Stable scan | Array insert | Array scan | Dynamic insert | Tiny insert | Large insert |",
    "| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |"]
for stage in stages:
    metrics = [("stable", "insert"), ("stable", "point"), ("stable", "scan"), ("arrays", "insert"), ("arrays", "scan"), ("dynamic", "insert"), ("tiny", "insert"), ("large", "insert")]
    lines.append("| " + stage + " | " + " | ".join(f"{median(stage, shape, metric, 'ms'):.2f}" for shape, metric in metrics) + " |")
lines += ["", "Stages: `boundary` introduces the BSON-only storage boundary; `compact-v1` adds opt-in codec/catalog/promotion/rebuild; `schema-cache` adds the bounded committed-reader cache; `admission` avoids encoding unseen scalar shapes, bypasses tiny documents and retains candidate hints across transactions; `fallback` adds dynamic backoff and the large-scalar saving threshold; `final` requires distinct document identities for schema admission so dynamic updates create no catalog; `final-legacy` measures the resulting implementation with compact writes disabled.", "",
    "## Final storage and WAL", "", "| Workload | BSON bytes | Compact bytes | Saving | BSON insert WAL | Compact insert WAL | BSON update WAL | Compact update WAL | Schema pages (bytes) |", "| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |"]
for shape in shapes:
    before, after = median("baseline", shape, "bytes"), median("final", shape, "bytes")
    values = [median(stage, shape, metric) for metric in ["wal", "updateWal"] for stage in ["baseline", "final"]]
    lines.append(f"| {shape} | {before:,} | {after:,} | {100*(1-after/before):.1f}% | " + " | ".join(f"{v:,}" for v in values) + f" | {median('final', shape, 'catalogBytes'):,} |")
lines += ["", "## Final operation timings (baseline → compact)", "", "| Workload | Insert | Update | 1,000 point reads | Full scan | Rebuild |", "| --- | ---: | ---: | ---: | ---: | ---: |"]
for shape in shapes:
    lines.append("| " + shape + " | " + " | ".join(f"{median('baseline', shape, metric, 'ms'):.2f} → {median('final', shape, metric, 'ms'):.2f}" for metric in ["insert", "update", "point", "scan", "rebuild"]) + " |")
lines += ["", "## Allocations and catalog cache", "", "| Workload | Insert allocated bytes (BSON → compact) | Scan allocated bytes (BSON → compact) | Accounted cache bytes |", "| --- | ---: | ---: | ---: |"]
for shape in shapes:
    values = [f"{median('baseline', shape, metric, 'allocated'):,} → {median('final', shape, metric, 'allocated'):,}" for metric in ["insert", "scan"]]
    lines.append("| " + shape + " | " + " | ".join(values) + f" | {median('final', shape, 'schemaCacheBytes'):,} |")
lines += ["", "## Larger rebuild", "", "100,000 stable documents, one warmup and three measured trials:", "", "| Stage | File bytes | Pages | Rebuild ms | Rebuilt bytes |", "| --- | ---: | ---: | ---: | ---: |"]
for stage in ["baseline-large", "final-large"]:
    rows = [json.loads(line) for line in (root / (stage + ".jsonl")).read_text().splitlines()]
    lines.append(f"| {stage} | {statistics.median(r['bytes'] for r in rows):,} | {statistics.median(r['pages'] for r in rows):,} | {statistics.median(r['rebuild']['ms'] for r in rows):.2f} | {statistics.median(r['rebuiltBytes'] for r in rows):,} |")
lines += ["", "## Interpretation and reproduction", "",
    "Stable, optional, nested and type-changing documents save roughly 49–57% of initial file bytes; arrays save 38%. Dynamic dictionaries, tiny documents and large scalar payloads keep BSON and allocate no schema pages. Compact stable/optional/nested inserts still cost more CPU, and converting existing BSON during mixed-file updates costs more than writing BSON again. Array scans and inserts are faster. The feature remains opt-in; these results do not justify changing the default.", "",
    "```sh", "dotnet build LiteDB/LiteDB.csproj -c Release -f net8.0 -p:TestingEnabled=false", "dotnet build tools/CompactStorage -c Release", "DOTNET_TieredCompilation=0 dotnet tools/CompactStorage/bin/Release/net8.0/CompactStorage.dll trial compact 5000 5", "DOTNET_TieredCompilation=0 dotnet tools/CompactStorage/bin/Release/net8.0/CompactStorage.dll large compact 100000 3 stable", "python3 scripts/summarize-compact-benchmarks.py", "```", "", "Pass `legacy` instead of `compact` for default writes. `-p:LiteDBAssembly=/absolute/path/LiteDB.dll` builds the same harness against a saved baseline/intermediate assembly. Use separate output directories when comparing assemblies. Raw JSONL includes every measured trial, page counts, catalog bytes, insert/update WAL, all timed operation allocations, and rebuild size.", ""]
(root / "README.md").write_text("\n".join(lines))
