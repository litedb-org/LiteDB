#!/usr/bin/env python3
"""Alternating native reader comparisons, with optional continuous commits/checkpoints."""
import argparse
import datetime
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import threading
import time

parser = argparse.ArgumentParser(description=__doc__)
for name in ('baseline', 'candidate', 'scratch', 'output'):
    parser.add_argument('--' + name, type=Path, required=True)
parser.add_argument('--rounds', type=int, default=5)
parser.add_argument('--smoke', action='store_true', help='Short validation pair only; not performance evidence')
parser.add_argument('--scenarios', nargs='+', choices=('point', 'medium', 'large'), default=('point', 'medium', 'large'))
parser.add_argument('--activity', nargs='+', choices=('idle', 'writer', 'checkpoint'), default=('idle', 'writer', 'checkpoint'))
parser.add_argument('--readers', nargs='+', type=int, choices=(1, 4), default=(1, 4))
args = parser.parse_args()
if args.smoke:
    args.rounds = 1
if args.rounds < 5 and not args.smoke:
    parser.error('At least five paired rounds are required')
args.scratch.mkdir(parents=True, exist_ok=True, mode=0o700)
args.output.parent.mkdir(parents=True, exist_ok=True)
env = dict(os.environ, TMPDIR=str(args.scratch.resolve()))
with args.output.open('x') as output:
    for scenario in args.scenarios:
        for activity in args.activity:
            for readers in args.readers:
                for pair in range(args.rounds):
                    for variant in (('baseline', 'candidate') if pair % 2 == 0 else ('candidate', 'baseline')):
                        directory = Path(tempfile.mkdtemp(prefix='readers-', dir=args.scratch.resolve()))
                        database, signal = directory / 'test.db', directory / 'start'
                        runner = getattr(args, variant).resolve() / 'SharedReadBenchmarks.dll'
                        record = dict(pair=pair, variant=variant, readers=readers, scenario=scenario, activity=activity,
                                      started=time.time(), TMPDIR=env['TMPDIR'], results=[], commands=[],
                                      librarySha256=hashlib.sha256((runner.parent/'LiteDB.dll').read_bytes()).hexdigest())
                        children = []
                        sampling_done = threading.Event()
                        sampling = None
                        def sample_wal():
                            # Run in the controller, outside every measured .NET process.
                            started_cpu = time.thread_time()
                            peak, count = 0, 0
                            wal = directory / 'test-log.db'
                            while not sampling_done.wait(.01):
                                try:
                                    peak = max(peak, wal.stat().st_size)
                                except FileNotFoundError:
                                    pass
                                count += 1
                            record['walSampling'] = dict(peakBytes=peak, count=count, intervalMs=10,
                                                        controllerCpuMs=(time.thread_time() - started_cpu) * 1000)
                        try:
                            seed = ['dotnet', str(runner), 'read-contention-seed', str(database)]
                            record['commands'].append(seed)
                            subprocess.run(seed, env=env, text=True, capture_output=True, check=True, timeout=30)
                            sampling = threading.Thread(target=sample_wal, daemon=True)
                            sampling.start()
                            roles = [scenario] * readers + ([] if activity == 'idle' else [activity])
                            for worker, role in enumerate(roles):
                                command = ['dotnet', str(runner), 'read-contention', str(database), str(worker), role,
                                           ('1000' if args.smoke else '10000'), ('2000' if args.smoke else '10000'),
                                           str(signal), str(readers), ('0' if role in ('writer', 'checkpoint') else '50' if role == 'large' else '20')]
                                record['commands'].append(command)
                                children.append(subprocess.Popen(command, env=env, text=True,
                                                                 stdout=subprocess.PIPE, stderr=subprocess.PIPE))
                            deadline = time.monotonic() + 30
                            while not all(Path(str(signal) + f'.ready-{i}').exists() for i in range(len(roles))):
                                if time.monotonic() > deadline or any(c.poll() is not None for c in children):
                                    raise RuntimeError('Worker failed to reach start barrier')
                                time.sleep(.01)
                            epoch = datetime.datetime(1, 1, 1, tzinfo=datetime.timezone.utc)
                            start = datetime.datetime.now(datetime.timezone.utc) + datetime.timedelta(milliseconds=500)
                            temporary = directory / 'publishing'
                            temporary.write_text(str(int((start - epoch).total_seconds() * 10_000_000)))
                            temporary.replace(signal)
                            for child in children:
                                stdout, stderr = child.communicate(timeout=60)
                                if child.returncode:
                                    raise RuntimeError(f'Worker exited {child.returncode}: {stderr}')
                                record['results'].append(json.loads(stdout))
                            revision = 0 if activity == 'idle' else record['results'][-1]['revision']
                            verify = ['dotnet', str(runner), 'read-contention-verify', str(database), str(revision)]
                            record['commands'].append(verify)
                            subprocess.run(verify, env=env, text=True, capture_output=True, check=True, timeout=30)
                            record['endWalBytes'] = sum(p.stat().st_size for p in directory.glob('*-log.db'))
                            if record['endWalBytes']:
                                raise RuntimeError('Final close left WAL content')
                            record['passed'] = True
                            sampling_done.set()
                            sampling.join()
                            shutil.rmtree(directory)
                        except Exception as error:
                            record['error'] = str(error)
                            if isinstance(error, subprocess.CalledProcessError):
                                record['stderr'] = error.stderr
                                record['stdout'] = error.stdout
                            record['retainedDirectory'] = str(directory)
                            raise
                        finally:
                            sampling_done.set()
                            if sampling is not None:
                                sampling.join()
                            for index, child in enumerate(children):
                                if child.poll() is None:
                                    child.kill()
                                stdout, stderr = child.communicate(timeout=5)
                                # Preserve diagnostics even for children that failed before the barrier.
                                (args.output.parent / (directory.name + f'-worker-{index}.log')).write_text(stdout + '\n' + stderr)
                            record['elapsed'] = time.time() - record['started']
                            output.write(json.dumps(record) + '\n')
                            output.flush()
                        print(scenario, activity, readers, pair, variant, 'validated', flush=True)
