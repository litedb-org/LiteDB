#!/usr/bin/env python3
"""Paired production pin/writer contention, including intended-arrival latency.

Each round also runs the contended-acquire scenario with the same writer counts and
alternating order: every process runs a fixed number of explicit transactions and
stamps arrival (before BeginTrans), acquisition (after it returns) and release (after
Commit) on the host's monotonic clock. A run is rejected unless ticket order equals
acquisition order, all stamps lie inside the driver's own reads of the same clock, a
fresh process verifies the final state and close leaves no WAL. Its records go to
--acquire-output; .github/scripts/compare_contention.py computes the metrics and
compares candidate with baseline.
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
import compare_contention as acquire_metrics  # noqa: E402

# Stopwatch.GetTimestamp reads these clocks; perf_counter must read the same one to bracket.
COMPARABLE_CLOCKS = {'Linux': 'clock_gettime(CLOCK_MONOTONIC)', 'Windows': 'QueryPerformanceCounter()'}

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--baseline', type=Path, required=True)
parser.add_argument('--candidate', type=Path, required=True)
parser.add_argument('--scratch', type=Path, required=True)
parser.add_argument('--output', type=Path, required=True)
parser.add_argument('--rounds', type=int, default=5)
parser.add_argument('--smoke', action='store_true', help='One short validation pair; not performance evidence')
parser.add_argument('--acquire-output', type=Path,
                    help='Contended-acquire records (default: <output stem>-acquire.jsonl beside --output)')
parser.add_argument('--acquire-transactions', type=int, default=400,
                    help='Measured acquisitions per acquire run, split evenly across the writers')
parser.add_argument('--acquire-warmup', type=int, default=20, help='Unmeasured transactions per acquire process')
parser.add_argument('--acquire-think-us', type=int, default=0, help='Fixed spin after each release (0 re-arrives)')
parser.add_argument('--no-acquire', action='store_true', help='Skip the contended-acquire scenario')
args = parser.parse_args()
if args.smoke:
    args.rounds, args.acquire_transactions = 1, 80
if args.rounds < 5 and not args.smoke:
    parser.error('At least five paired rounds are required')
if args.acquire_transactions < 40:
    parser.error('--acquire-transactions must be at least 40')
args.acquire_output = args.acquire_output or args.output.with_name(args.output.stem + '-acquire.jsonl')
args.scratch.mkdir(parents=True, exist_ok=True, mode=0o700)
args.output.parent.mkdir(parents=True, exist_ok=True)
env = dict(os.environ, TMPDIR=str(args.scratch.resolve()))
clock = time.get_clock_info('perf_counter').implementation
bracketed = COMPARABLE_CLOCKS.get(platform.system()) == clock


def start_barrier(directory, signal, children, count, delay_ms, ready_seconds):
    """Wait for every worker's ready file, then atomically publish a common UTC start time."""
    deadline = time.monotonic() + ready_seconds
    while not all(Path(str(signal) + f'.ready-{i}').exists() for i in range(count)):
        if time.monotonic() > deadline or any(c.poll() is not None for c in children):
            raise RuntimeError('Worker failed to reach start barrier')
        time.sleep(.01)
    epoch = datetime.datetime(1, 1, 1, tzinfo=datetime.timezone.utc)
    start = datetime.datetime.now(datetime.timezone.utc) + datetime.timedelta(milliseconds=delay_ms)
    temporary = directory / 'publishing'
    temporary.write_text(str(int((start - epoch).total_seconds() * 10_000_000)))
    temporary.replace(signal)


def run_acquire(output, pair, variant, processes):
    """One contended-acquire run; returns its metrics. Raises (keeping the files) when unverified."""
    directory = Path(tempfile.mkdtemp(prefix='acquire-', dir=args.scratch.resolve()))
    database, signal = directory / 'test.db', directory / 'start'
    runner = getattr(args, variant).resolve() / 'SharedReadBenchmarks.dll'
    iterations, warmup = args.acquire_transactions // processes, args.acquire_warmup
    started = time.time()
    record = dict(pair=pair, variant=variant, processes=processes, iterations=iterations, warmup=warmup,
                  thinkUs=args.acquire_think_us, started=started, host=platform.node(), clock=clock,
                  librarySha256=hashlib.sha256((runner.parent / 'LiteDB.dll').read_bytes()).hexdigest(),
                  TMPDIR=env['TMPDIR'], commands=[], workers=[])
    children = []
    try:
        for worker in range(processes):
            command = ['dotnet', str(runner), 'acquire', str(database), str(worker), str(warmup), str(iterations),
                       str(args.acquire_think_us), str(signal), str(directory / f'samples-{worker}.json')]
            record['commands'].append(command)
            children.append(subprocess.Popen(command, env=env, text=True,
                                             stdout=subprocess.PIPE, stderr=subprocess.PIPE))
        start_barrier(directory, signal, children, processes, 300, 60)
        before = time.perf_counter_ns()
        for child in children:
            _, stderr = child.communicate(timeout=120)
            if child.returncode:
                raise RuntimeError(f'Acquire worker exited {child.returncode}: {stderr}')
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
        problems = acquire_metrics.check_samples(samples, record['frequency'], processes, iterations,
                                                 bracket_ns=record['bracketNs'])
        if problems:
            raise RuntimeError('Inconsistent acquire samples: ' + '; '.join(problems[:5]))
        subprocess.run(['dotnet', str(runner), 'acquire-verify', str(database), str(processes),
                        str(warmup + iterations)], env=env, text=True, capture_output=True, check=True, timeout=60)
        record['endWalBytes'] = sum(p.stat().st_size for p in directory.glob('*-log.db'))
        if record['endWalBytes']:
            raise RuntimeError('Final close left WAL content')
        record['samples'] = samples
        record['metrics'] = acquire_metrics.metrics(samples, record['frequency'])
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


acquire_output = open(os.devnull, 'w') if args.no_acquire else args.acquire_output.open('x')
with args.output.open('x') as output, acquire_output:
    for writers in (2, 4):
        for pair in range(args.rounds):
            for variant in (('baseline', 'candidate') if pair % 2 == 0 else ('candidate', 'baseline')):
                directory = Path(tempfile.mkdtemp(prefix='contention-', dir=args.scratch.resolve()))
                database = directory / 'test.db'
                signal = directory / 'start'
                runner = getattr(args, variant).resolve() / 'SharedReadBenchmarks.dll'
                started = time.time()
                record = dict(pair=pair, variant=variant, writers=writers, started=started,
                              librarySha256=hashlib.sha256((runner.parent/'LiteDB.dll').read_bytes()).hexdigest(),
                              TMPDIR=env['TMPDIR'], commands=[], results=[])
                children = []
                try:
                    subprocess.run(['dotnet', str(runner), 'interop', str(database)], input='write 0\nexit\n',
                                   text=True, capture_output=True, env=env, check=True, timeout=30)
                    for worker in range(writers):
                        command = ['dotnet', str(runner), 'contention', str(database), str(worker),
                                   ('2000' if args.smoke else '10000'), str(worker * 5), str(signal)]
                        record['commands'].append(command)
                        children.append(subprocess.Popen(command, env=env, text=True,
                                                         stdout=subprocess.PIPE, stderr=subprocess.PIPE))
                    # Publish the complete barrier value atomically; all workers share this host clock.
                    start_barrier(directory, signal, children, writers, 500, 30)
                    for child in children:
                        stdout, stderr = child.communicate(timeout=45)
                        if child.returncode:
                            raise RuntimeError(f'Worker exited {child.returncode}: {stderr}')
                        record['results'].append(json.loads(stdout))
                    counts = ','.join(str(r['count']) for r in record['results'])
                    subprocess.run(['dotnet', str(runner), 'contention-verify', str(database), counts],
                                   env=env, text=True, capture_output=True, check=True, timeout=30)
                    record['endWalBytes'] = sum(p.stat().st_size for p in directory.glob('*-log.db'))
                    if record['endWalBytes']:
                        raise RuntimeError('Final close left WAL content')
                    record['passed'] = True
                    shutil.rmtree(directory)
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
                print(writers, pair, variant, 'validated', flush=True)
                if not args.no_acquire:
                    result = run_acquire(acquire_output, pair, variant, writers)
                    print(writers, pair, variant, 'acquire validated', json.dumps(
                        {key: result[key] for key in ('acquisitions', 'p50Ms', 'p99Ms', 'maxWaitMs', 'overtakingRate',
                                                      'handoffP50Ms', 'othersWhileWaiting')}), flush=True)
