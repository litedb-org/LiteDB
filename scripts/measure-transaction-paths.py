#!/usr/bin/env python3
"""Serial, alternating fresh-process comparisons of transaction paths.

Build the TransactionPathBenchmarks runner against each production LiteDB.dll first; the
candidate runner with -p:Handles=true. Every workload runs as one process per variant and
round, and the variant order alternates between rounds. Tables report medians of process
results and the median (range) of round-paired ratios.

- Common workloads use pre-handle APIs only: baseline runner vs candidate runner.
- Handle workloads (candidate only) alternate with their legacy adapter on the candidate
  runner: "New-handle cost vs legacy adapter (same binary)".
- Resource workloads (candidate only) assert their own bounds; one process per round.
- --aa runs the candidate against itself (and each handle workload against itself): the
  per-workload noise floor from which budgets are derived.
"""
import argparse
import hashlib
import json
from pathlib import Path
import platform
import statistics
import subprocess
import time

CONTENTION = [f'shared-contention-{kind}-{n}' for kind in ('ordinary', 'legacy') for n in (2, 4, 8)]
COMMON = ['direct-ordinary-read', 'direct-legacy-read', 'direct-ordinary-update',
          'shared-ordinary-read', 'shared-legacy-read', 'shared-ordinary-update',
          'direct-legacy-update', 'shared-legacy-update', 'shared-legacy-empty',
          'direct-open-close', 'shared-open-close', *CONTENTION, 'shared-contention-2proc']
ADAPTER = {'direct-handle-read': 'direct-legacy-read', 'shared-handle-read': 'shared-legacy-read',
           'direct-handle-update': 'direct-legacy-update', 'shared-handle-update': 'shared-legacy-update',
           'shared-handle-empty': 'shared-legacy-empty',
           **{f'shared-contention-handle-{n}': f'shared-contention-legacy-{n}' for n in (2, 4, 8)}}
# Direct handles are limited by the engine's 100 open transactions (MAX_OPEN_TRANSACTIONS).
RESOURCES = ['shared-pending-begins-1', 'shared-pending-begins-4', 'shared-pending-begins-16',
             'direct-idle-handles-16', 'direct-idle-handles-96']
CANDIDATE_ONLY = {*ADAPTER, *RESOURCES}
WORKLOADS = COMMON + list(ADAPTER) + RESOURCES

parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
parser.add_argument('--baseline', type=Path, help='Baseline runner directory (not used with --aa)')
parser.add_argument('--candidate', type=Path, required=True, help='Candidate runner directory (built with Handles=true)')
parser.add_argument('--scratch', type=Path, required=True)
parser.add_argument('--output', type=Path, required=True)
parser.add_argument('--summary', type=Path, help='Markdown summary to append to')
parser.add_argument('--rounds', type=int, default=5)
parser.add_argument('--workloads', nargs='+', default=WORKLOADS, choices=WORKLOADS)
parser.add_argument('--aa', action='store_true', help='Candidate against itself: the noise floor')
parser.add_argument('--warmup', type=float, default=3, help='Warmup seconds per process')
parser.add_argument('--windows', type=int, default=5, help='Timed windows per process')
parser.add_argument('--window-seconds', type=float, default=1, help='Length of one timed window')
parser.add_argument('--smoke', action='store_true', help='Allow fewer rounds; the output is not evidence')
args = parser.parse_args()
if args.rounds < (2 if args.smoke else 4):
    parser.error('At least four rounds are required (two with --smoke)')
if not args.aa and args.baseline is None:
    parser.error('--baseline is required unless --aa is given')
args.scratch.mkdir(parents=True, exist_ok=True)
args.output.parent.mkdir(parents=True, exist_ok=True)
runners = {'baseline': args.candidate if args.aa else args.baseline, 'candidate': args.candidate}
timing = [f'{args.warmup:g}', str(args.windows), f'{args.window_seconds:g}']


def plan(workload):
    """The (variant, runner, runner workload) pair compared for one workload."""
    if workload in ADAPTER:
        adapter = workload if args.aa else ADAPTER[workload]
        return [('adapter', 'candidate', adapter), ('handle', 'candidate', workload)]
    if workload in RESOURCES:
        return [('candidate', 'candidate', workload)]
    return [('baseline', 'baseline', workload), ('candidate', 'candidate', workload)]


results = {}
with args.output.open('x') as output:
    for workload in args.workloads:
        variants = plan(workload)
        for pair in range(args.rounds):
            for variant, runner, name in (variants if pair % 2 == 0 else variants[::-1]):
                dll = runners[runner].resolve() / 'TransactionPathBenchmarks.dll'
                command = ['dotnet', str(dll), str(args.scratch.resolve()), name, *timing]
                started = time.time()
                result = subprocess.run(command, text=True, capture_output=True, timeout=300)
                record = dict(workload=workload, pair=pair, variant=variant, runnerWorkload=name, aa=args.aa,
                              command=command,
                              librarySha256=hashlib.sha256((dll.parent / 'LiteDB.dll').read_bytes()).hexdigest(),
                              started=started, elapsed=time.time() - started, host=platform.platform(),
                              returncode=result.returncode, stderr=result.stderr)
                lines = result.stdout.strip().splitlines()
                if lines:
                    try:
                        record['result'] = json.loads(lines[-1])
                    except json.JSONDecodeError:
                        record['stdout'] = result.stdout
                output.write(json.dumps(record) + '\n')
                output.flush()
                if result.returncode != 0 or 'result' not in record:
                    raise SystemExit(f'{variant} {name} failed ({result.returncode}):\n{result.stdout}\n{result.stderr}')
                results.setdefault(workload, {}).setdefault(variant, {})[pair] = record['result']

median = statistics.median


def latency(records):
    return ' / '.join(f"{median([r[key] for r in records]):,.0f}" for key in ('p50us', 'p90us', 'p99us', 'p999us'))


def compare(title, names, a, b):
    lines = [f'#### {title}', '',
             f'| Workload | {a} ops/s | {b} ops/s | Paired change (range) | {a} p50/p90/p99/p99.9 µs | '
             f'{b} p50/p90/p99/p99.9 µs | {a} B/op | {b} B/op | {b} contention |',
             '| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |']
    for workload in names:
        first, second = results[workload][a], results[workload][b]
        ratios = [second[p]['opsPerSecond'] / first[p]['opsPerSecond'] - 1 for p in first]
        rows = list(second.values())
        notes = ''
        if 'workerMinOps' in rows[0]:
            notes = (f"workers {rows[0]['workers']}: ops {min(r['workerMinOps'] for r in rows)}…"
                     f"{max(r['workerMaxOps'] for r in rows)}; peak threads +"
                     f"{max(r['peakThreads'] - r['threadsBefore'] for r in rows)}, handles +"
                     f"{max(r['peakHandles'] - r['handlesBefore'] for r in rows)}")
        elif 'child' in rows[0]:
            notes = f"child {median([r['child']['opsPerSecond'] for r in rows]):,.0f} ops/s"
        lines.append(f"| {workload} | {median([r['opsPerSecond'] for r in first.values()]):,.0f} | "
                     f"{median([r['opsPerSecond'] for r in rows]):,.0f} | "
                     f"{median(ratios) * 100:+.1f}% ({min(ratios) * 100:+.1f}…{max(ratios) * 100:+.1f}%) | "
                     f"{latency(first.values())} | {latency(rows)} | "
                     f"{median([r['bytesPerOperation'] for r in first.values()]):,.0f} | "
                     f"{median([r['bytesPerOperation'] for r in rows]):,.0f} | {notes} |")
    return '\n'.join(lines)


def resources(names):
    lines = ['#### Handle resources (candidate)', '',
             '| Workload | Passed | Peak threads Δ (bound) | Drain non-caller threads | After Δ threads / handles / managed B | '
             'Idle B/handle | Completed B/handle | Peak private MB |',
             '| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |']
    for workload in names:
        rows = list(results[workload]['candidate'].values())
        peak = (f"+{max(r['peakThreadDelta'] for r in rows)} ({rows[0]['threadBound']})"
                if 'peakThreadDelta' in rows[0] else '')
        drain = str(max(r['drainNonCallerThreads'] for r in rows)) if 'drainNonCallerThreads' in rows[0] else ''
        idle = f"{median([r['bytesPerIdleHandle'] for r in rows]):,.0f}" if 'bytesPerIdleHandle' in rows[0] else ''
        done = f"{median([r['bytesPerCompletedHandle'] for r in rows]):,.0f}" if 'bytesPerCompletedHandle' in rows[0] else ''
        after = (f"{max(r['afterThreadDelta'] for r in rows):+d} / {max(r['afterHandleDelta'] for r in rows):+d} / "
                 f"{max(r['afterManagedDelta'] for r in rows):+,d}")
        private = max(r['peak']['privateBytes'] for r in rows) / 2 ** 20
        lines.append(f"| {workload} | {all(r['passed'] for r in rows)} | {peak} | {drain} | {after} | {idle} | {done} | "
                     f"{private:,.1f} |")
    return '\n'.join(lines)


label = 'A/A noise floor: candidate vs candidate' if args.aa else 'Baseline vs candidate'
tables = []
common = [w for w in args.workloads if w not in CANDIDATE_ONLY]
handles = [w for w in args.workloads if w in ADAPTER]
owned = [w for w in args.workloads if w in RESOURCES]
if common:
    tables.append(compare(f'{label} (pre-handle API)', common, 'baseline', 'candidate'))
if handles:
    title = 'A/A noise floor: handle vs handle (same binary)' if args.aa else 'New-handle cost vs legacy adapter (same binary)'
    tables.append(compare(title, handles, 'adapter', 'handle'))
if owned:
    tables.append(resources(owned))
table = '\n\n'.join(tables)
print(table)
if args.summary:
    note = ', SMOKE: not evidence' if args.smoke else ''
    with args.summary.open('a') as summary:
        summary.write(f"### {platform.platform()} ({args.rounds} alternating rounds{', A/A' if args.aa else ''}{note})\n\n"
                      f"{table}\n\n")
