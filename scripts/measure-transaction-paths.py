#!/usr/bin/env python3
"""Serial, alternating fresh-process comparisons of ordinary and legacy transaction paths.

Build the identical TransactionPathBenchmarks runner against each production LiteDB.dll
first. Every workload runs as one process per variant and round; the variant order
alternates between rounds. The summary reports medians of process results and the median
of round-paired candidate/baseline ratios.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import statistics
import subprocess
import time

WORKLOADS = ['direct-ordinary-read', 'direct-legacy-read', 'direct-ordinary-update',
             'shared-ordinary-read', 'shared-legacy-read', 'shared-ordinary-update']

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--baseline', type=Path, required=True, help='Baseline runner directory')
parser.add_argument('--candidate', type=Path, required=True, help='Candidate runner directory')
parser.add_argument('--scratch', type=Path, required=True)
parser.add_argument('--output', type=Path, required=True)
parser.add_argument('--summary', type=Path, help='Markdown summary to append to')
parser.add_argument('--rounds', type=int, default=5)
parser.add_argument('--workloads', nargs='+', default=WORKLOADS, choices=WORKLOADS)
args = parser.parse_args()
if args.rounds < 4:
    parser.error('At least four rounds are required')
args.scratch.mkdir(parents=True, exist_ok=True)
args.output.parent.mkdir(parents=True, exist_ok=True)
results = {}
with args.output.open('x') as output:
    for workload in args.workloads:
        for pair in range(args.rounds):
            for variant in (('baseline', 'candidate') if pair % 2 == 0 else ('candidate', 'baseline')):
                dll = getattr(args, variant).resolve() / 'TransactionPathBenchmarks.dll'
                command = ['dotnet', str(dll), str(args.scratch.resolve()), workload]
                started = time.time()
                result = subprocess.run(command, text=True, capture_output=True, timeout=300)
                record = dict(workload=workload, pair=pair, variant=variant, command=command,
                              librarySha256=hashlib.sha256((dll.parent / 'LiteDB.dll').read_bytes()).hexdigest(),
                              started=started, elapsed=time.time() - started, host=platform.platform(),
                              returncode=result.returncode, stderr=result.stderr)
                if result.returncode == 0:
                    record['result'] = json.loads(result.stdout)
                output.write(json.dumps(record) + '\n')
                output.flush()
                if result.returncode != 0:
                    raise SystemExit(f'{variant} {workload} failed:\n{result.stderr}')
                results.setdefault(workload, {}).setdefault(variant, {})[pair] = record['result']

lines = ['| Workload | Baseline ops/s | Candidate ops/s | Paired change (range) | Baseline B/op | Candidate B/op |',
         '| --- | ---: | ---: | ---: | ---: | ---: |']
for workload in args.workloads:
    base, cand = results[workload]['baseline'], results[workload]['candidate']
    ratios = [cand[p]['opsPerSecond'] / base[p]['opsPerSecond'] - 1 for p in base]
    median = lambda values: statistics.median(values)
    lines.append(f"| {workload} | {median([r['opsPerSecond'] for r in base.values()]):,.0f} | "
                 f"{median([r['opsPerSecond'] for r in cand.values()]):,.0f} | "
                 f"{median(ratios) * 100:+.1f}% ({min(ratios) * 100:+.1f}…{max(ratios) * 100:+.1f}%) | "
                 f"{median([r['bytesPerOperation'] for r in base.values()]):,.0f} | "
                 f"{median([r['bytesPerOperation'] for r in cand.values()]):,.0f} |")
table = '\n'.join(lines)
print(table)
if args.summary:
    with args.summary.open('a') as summary:
        summary.write(f"### {platform.platform()} ({args.rounds} alternating rounds)\n\n{table}\n\n")
