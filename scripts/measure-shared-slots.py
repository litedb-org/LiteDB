#!/usr/bin/env python3
"""Serial, alternating production comparisons; build runner DLLs separately."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import subprocess
import time

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--baseline', type=Path, required=True, help='Baseline runner directory')
parser.add_argument('--candidate', type=Path, required=True, help='Candidate runner directory')
parser.add_argument('--scratch', type=Path, required=True)
parser.add_argument('--output', type=Path, required=True)
parser.add_argument('--rounds', type=int, default=5)
args = parser.parse_args()
if args.rounds < 5:
    parser.error('At least five paired rounds are required')
args.scratch.mkdir(parents=True, exist_ok=True, mode=0o700)
args.output.parent.mkdir(parents=True, exist_ok=True)
env = dict(os.environ, TMPDIR=str(args.scratch.resolve()))
workloads = [('point', 20000, 10), ('scan', 1000, 10), ('mixed', 3000, 10)]
workloads += [('slots', 100000, active) for active in (1, 64, 4096, 65536)]
# Exclusive creation protects previous evidence from accidental replacement.
with args.output.open('x') as output:
    for scenario, count, parameter in workloads:
        for pair in range(args.rounds):
            order = ('baseline', 'candidate') if pair % 2 == 0 else ('candidate', 'baseline')
            for variant in order:
                dll = getattr(args, variant).resolve() / 'SharedReadBenchmarks.dll'
                command = ['dotnet', str(dll), str(args.scratch.resolve()),
                           'shared', scenario, str(count), str(parameter)]
                started = time.time()
                result = subprocess.run(command, env=env, text=True, capture_output=True, timeout=300)
                record = dict(pair=pair, variant=variant, command=command, TMPDIR=env['TMPDIR'],
                              librarySha256=hashlib.sha256((dll.parent / 'LiteDB.dll').read_bytes()).hexdigest(),
                              runnerSha256=hashlib.sha256(dll.read_bytes()).hexdigest(),
                              started=started, elapsed=time.time()-started, host=platform.platform(),
                              returncode=result.returncode, stderr=result.stderr)
                if result.returncode == 0:
                    record['result'] = json.loads(result.stdout)
                else:
                    record['stdout'] = result.stdout
                output.write(json.dumps(record) + '\n')
                output.flush()
                print(scenario, parameter, pair, variant, result.returncode, flush=True)
                if result.returncode != 0:
                    raise SystemExit('Measurement failed; evidence preserved')
