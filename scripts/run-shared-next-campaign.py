#!/usr/bin/env python3
"""Bounded serial Shared/MVCC campaign, preserving each command and first failure."""
import argparse
import json
import os
from pathlib import Path
import signal
import subprocess
import time

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--runner', type=Path, required=True)
parser.add_argument('--output', type=Path, required=True)
parser.add_argument('--scratch', type=Path, required=True)
parser.add_argument('--smoke', action='store_true')
parser.add_argument('--budget-root', type=Path, help='Shared accounting root for smoke and extended artifacts')
args = parser.parse_args()
args.output.mkdir(parents=True, exist_ok=True)
args.scratch.mkdir(parents=True, exist_ok=True, mode=0o700)
env = dict(os.environ, TMPDIR=str(args.scratch.resolve()))
seeds = range(3012, 3015 if args.smoke else 3028)
count = 4 if args.smoke else 64
root = args.output.resolve()
budget_root = (args.budget_root or args.output).resolve()
if not root.is_relative_to(budget_root):
    parser.error('--output must be inside --budget-root')

def size(path):
    return sum(p.stat().st_size for p in path.rglob('*') if p.is_file() and not p.is_symlink())

with (root / 'commands.jsonl').open('x') as manifest:
    for seed in seeds:
        for target in ('snapshot', 'shared', 'mvcc-retirement', 'index'):
            if size(budget_root) >= 480 * 1024**2:
                raise SystemExit('External 512 MiB budget nearly reached; stopping safely')
            command = ['dotnet', str(args.runner.resolve()), '--target', target,
                       '--seed', str(seed), '--count', str(count), '--workers', '1',
                       '--max-artifact-mb', '256', '--artifact-dir', str(root / 'runs' / f'{target}-{seed}')]
            # Each invocation also replays its artifact directory's interesting
            # corpus. Isolate finite cases so later seeds do not recursively rerun
            # every earlier case and exceed the per-session time budget. Built-in
            # regression inputs still run; all partitions share the external cap.
            started = time.time()
            record = dict(command=command, TMPDIR=env['TMPDIR'], started=started)
            with (root / f'{target}-{seed}.log').open('x') as log:
                # This task owns the freshly created session; never signal unrelated jobs.
                child = subprocess.Popen(command, env=env, stdout=log, stderr=subprocess.STDOUT,
                                         start_new_session=True)
                try:
                    record['returncode'] = child.wait(timeout=290)
                except subprocess.TimeoutExpired:
                    if child.poll() is None and os.getpgid(child.pid) == child.pid:
                        os.killpg(child.pid, signal.SIGTERM)
                        try:
                            child.wait(timeout=5)
                        except subprocess.TimeoutExpired:
                            os.killpg(child.pid, signal.SIGKILL)
                            child.wait(timeout=5)
                    record['timeout'] = True
                    record['returncode'] = child.returncode
            record['elapsed'] = time.time() - started
            record['artifactBytes'] = size(budget_root)
            manifest.write(json.dumps(record) + '\n')
            manifest.flush()
            print(target, seed, record['returncode'], round(record['elapsed'], 1), flush=True)
            if record.get('timeout') or record['returncode']:
                raise SystemExit('Campaign failed; original evidence preserved, no retry')
