#!/usr/bin/env python3
"""Paired production pin/writer contention, including intended-arrival latency."""
import argparse
import datetime
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import time

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--baseline', type=Path, required=True)
parser.add_argument('--candidate', type=Path, required=True)
parser.add_argument('--scratch', type=Path, required=True)
parser.add_argument('--output', type=Path, required=True)
parser.add_argument('--rounds', type=int, default=5)
parser.add_argument('--smoke', action='store_true', help='One short validation pair; not performance evidence')
args = parser.parse_args()
if args.smoke:
    args.rounds = 1
if args.rounds < 5 and not args.smoke:
    parser.error('At least five paired rounds are required')
args.scratch.mkdir(parents=True, exist_ok=True, mode=0o700)
args.output.parent.mkdir(parents=True, exist_ok=True)
env = dict(os.environ, TMPDIR=str(args.scratch.resolve()))
with args.output.open('x') as output:
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
                    deadline = time.monotonic() + 30
                    while not all(Path(str(signal) + f'.ready-{i}').exists() for i in range(writers)):
                        if time.monotonic() > deadline or any(c.poll() is not None for c in children):
                            raise RuntimeError('Worker failed to reach start barrier')
                        time.sleep(.01)
                    # Publish the complete barrier value atomically; all workers share this host clock.
                    epoch = datetime.datetime(1, 1, 1, tzinfo=datetime.timezone.utc)
                    start = datetime.datetime.now(datetime.timezone.utc) + datetime.timedelta(milliseconds=500)
                    ticks = int((start - epoch).total_seconds() * 10_000_000)
                    temporary = directory / 'publishing'
                    temporary.write_text(str(ticks))
                    temporary.replace(signal)
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
