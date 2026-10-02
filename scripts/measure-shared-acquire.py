#!/usr/bin/env python3
"""Paired production contended-writer acquisition: latency, max wait and starvation.

N worker processes share one Shared-mode database and each run a fixed number of
explicit transactions, stamping arrival (before BeginTrans), acquisition (after it
returns) and release (after Commit) on the host's monotonic clock. Rounds alternate
baseline/candidate order. Every run is verified: ticket order must equal acquisition
order, all stamps must fall inside the driver's own reads of the same clock, a fresh
process checks the acknowledged final state, and final close must leave no WAL.
Metrics and the base-vs-head gate live in .github/scripts/compare_contention.py.
"""
import argparse
import datetime
import hashlib
import json
import os
from pathlib import Path
import platform
import shutil
import subprocess
import sys
import tempfile
import time

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / '.github' / 'scripts'))
import compare_contention as contention  # noqa: E402

# Stopwatch.GetTimestamp reads these clocks; perf_counter must read the same one to bracket.
COMPARABLE_CLOCKS = {'Linux': 'clock_gettime(CLOCK_MONOTONIC)', 'Windows': 'QueryPerformanceCounter()'}

parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
parser.add_argument('--baseline', type=Path, required=True)
parser.add_argument('--candidate', type=Path, required=True)
parser.add_argument('--scratch', type=Path, required=True)
parser.add_argument('--output', type=Path, required=True)
parser.add_argument('--rounds', type=int, default=5)
parser.add_argument('--processes', type=int, nargs='+', default=[2, 4])
parser.add_argument('--transactions', type=int, default=400,
                    help='Measured acquisitions per run, split evenly across the processes')
parser.add_argument('--warmup', type=int, default=20, help='Unmeasured transactions per process before the barrier')
parser.add_argument('--think-us', type=int, default=0, help='Fixed spin after each release (0 re-arrives at once)')
parser.add_argument('--candidate-only', action='store_true', help='Measure only the candidate (no pairs)')
parser.add_argument('--smoke', action='store_true', help='One short validation pair; not performance evidence')
args = parser.parse_args()
if args.smoke:
    args.rounds, args.transactions = 1, 80
if args.rounds < 5 and not args.smoke:
    parser.error('At least five paired rounds are required')
if any(count < 2 or count > 4 for count in args.processes):
    parser.error('Use two to four processes')
if args.transactions < 40:
    parser.error('--transactions must be at least 40')
args.scratch.mkdir(parents=True, exist_ok=True, mode=0o700)
args.output.parent.mkdir(parents=True, exist_ok=True)
env = dict(os.environ, TMPDIR=str(args.scratch.resolve()))
clock = time.get_clock_info('perf_counter').implementation
bracketed = COMPARABLE_CLOCKS.get(platform.system()) == clock


def run_once(output, pair, variant, processes):
    directory = Path(tempfile.mkdtemp(prefix='acquire-', dir=args.scratch.resolve()))
    database, signal = directory / 'test.db', directory / 'start'
    runner = getattr(args, variant).resolve() / 'SharedReadBenchmarks.dll'
    iterations = args.transactions // processes
    started = time.time()
    record = dict(pair=pair, variant=variant, processes=processes, iterations=iterations, warmup=args.warmup,
                  thinkUs=args.think_us, started=started, host=platform.node(), clock=clock,
                  librarySha256=hashlib.sha256((runner.parent / 'LiteDB.dll').read_bytes()).hexdigest(),
                  TMPDIR=env['TMPDIR'], commands=[], workers=[])
    children = []
    try:
        for worker in range(processes):
            command = ['dotnet', str(runner), 'acquire', str(database), str(worker), str(args.warmup),
                       str(iterations), str(args.think_us), str(signal), str(directory / f'samples-{worker}.json')]
            record['commands'].append(command)
            children.append(subprocess.Popen(command, env=env, text=True,
                                             stdout=subprocess.PIPE, stderr=subprocess.PIPE))
        deadline = time.monotonic() + 60
        while not all(Path(f'{signal}.ready-{i}').exists() for i in range(processes)):
            if time.monotonic() > deadline or any(c.poll() is not None for c in children):
                raise RuntimeError('Worker failed to reach start barrier')
            time.sleep(.01)
        before = time.perf_counter_ns()
        # Publish the complete barrier value atomically; all workers share this host clock.
        epoch = datetime.datetime(1, 1, 1, tzinfo=datetime.timezone.utc)
        start = datetime.datetime.now(datetime.timezone.utc) + datetime.timedelta(milliseconds=300)
        temporary = directory / 'publishing'
        temporary.write_text(str(int((start - epoch).total_seconds() * 10_000_000)))
        temporary.replace(signal)
        for child in children:
            _, stderr = child.communicate(timeout=120)
            if child.returncode:
                raise RuntimeError(f'Worker exited {child.returncode}: {stderr}')
        after = time.perf_counter_ns()
        samples, frequency = [], set()
        for worker in range(processes):
            data = json.loads((directory / f'samples-{worker}.json').read_text())
            frequency.add(data['frequency'])
            record['workers'].append({key: data[key] for key in ('worker', 'frequency', 'isHighResolution')})
            samples += [[worker, *sample] for sample in data['samples']]
        if len(frequency) != 1:
            raise RuntimeError(f'Workers report different Stopwatch frequencies: {sorted(frequency)}')
        record['frequency'] = frequency.pop()
        record['bracketNs'] = [before, after] if bracketed else None
        problems = contention.check_samples(samples, record['frequency'], processes, iterations,
                                            bracket_ns=record['bracketNs'])
        if problems:
            raise RuntimeError('Inconsistent samples: ' + '; '.join(problems[:5]))
        subprocess.run(['dotnet', str(runner), 'acquire-verify', str(database), str(processes),
                        str(args.warmup + iterations)], env=env, text=True, capture_output=True, check=True,
                       timeout=60)
        record['endWalBytes'] = sum(p.stat().st_size for p in directory.glob('*-log.db'))
        if record['endWalBytes']:
            raise RuntimeError('Final close left WAL content')
        record['samples'] = samples
        record['metrics'] = contention.metrics(samples, record['frequency'])
        record['passed'] = True
        shutil.rmtree(directory)
        return record['metrics']
    except Exception as error:
        record['error'] = str(error)
        record['retainedDirectory'] = str(directory)
        raise
    finally:
        for child in children:
            if child.poll() is None:
                child.kill()
                child.wait(timeout=5)
        record['elapsed'] = time.time() - started
        output.write(json.dumps(record) + '\n')
        output.flush()


with args.output.open('x') as output:
    for processes in args.processes:
        for pair in range(args.rounds):
            order = ('baseline', 'candidate') if pair % 2 == 0 else ('candidate', 'baseline')
            for variant in (('candidate',) if args.candidate_only else order):
                result = run_once(output, pair, variant, processes)
                print(processes, pair, variant, 'validated', json.dumps(
                    {key: result[key] for key in ('acquisitions', 'p50Ms', 'p99Ms', 'maxWaitMs',
                                                  'starvationCount', 'starvationRate', 'handoffP50Ms')}), flush=True)
