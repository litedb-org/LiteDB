#!/usr/bin/env python3
"""Exercise old/candidate production binaries with three held reader generations."""
import argparse
import json
import os
from pathlib import Path
import selectors
import subprocess
import tempfile

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--baseline', type=Path, required=True)
parser.add_argument('--candidate', type=Path, required=True)
parser.add_argument('--scratch', type=Path, required=True)
args = parser.parse_args()
args.scratch.mkdir(parents=True, exist_ok=True, mode=0o700)
env = dict(os.environ, TMPDIR=str(args.scratch.resolve()))

class Client:
    def __init__(self, runner, database):
        self.errors = tempfile.TemporaryFile(mode='w+t')
        self.child = subprocess.Popen(['dotnet', str(runner.resolve() / 'SharedReadBenchmarks.dll'),
                                       'interop', str(database)], stdin=subprocess.PIPE,
                                      stdout=subprocess.PIPE, stderr=self.errors, text=True, env=env)
        self.selector = selectors.DefaultSelector()
        self.selector.register(self.child.stdout, selectors.EVENT_READ)

    def send(self, command):
        self.child.stdin.write(command + '\n')
        self.child.stdin.flush()
        if not self.selector.select(timeout=30):
            raise RuntimeError('Timed out: ' + command)
        answer = self.child.stdout.readline().strip()
        if answer != command:
            self.errors.seek(0)
            raise RuntimeError(f'{command}: {answer}: {self.errors.read()}')

    def close(self):
        if self.child.poll() is None:
            try:
                self.child.stdin.write('exit\n')
                self.child.stdin.flush()
                self.child.wait(timeout=10)
            finally:
                if self.child.poll() is None:
                    self.child.kill()
                    self.child.wait(timeout=5)
        self.selector.close()
        self.errors.close()
        if self.child.returncode:
            raise RuntimeError('Child failed: ' + str(self.child.returncode))

# Keep failed databases for diagnosis; delete successful fixtures only.
for writer_name, reader_name in [('baseline', 'candidate'), ('candidate', 'baseline')]:
    directory = Path(tempfile.mkdtemp(prefix='interop-', dir=args.scratch.resolve()))
    database = directory / 'test.db'
    clients = []
    try:
        writer = Client(getattr(args, writer_name), database)
        clients.append(writer)
        writer.send('write 0')
        reader = Client(getattr(args, reader_name), database)
        clients.append(reader)
        for epoch in range(4):
            first = epoch * 8
            writer.send(f'write {first}')
            reader.send(f'open {first}')
            writer.send(f'write {first + 1}')
            reader.send(f'open {first + 1}')
            writer.send(f'write {first + 2}')
            reader.send(f'open {first + 2}')
            for generation in range(first + 3, first + 6):
                writer.send(f'write {generation}')
                writer.send('checkpoint')
                writer.send(f'verify {generation}')
            reader.send(f'release {first}')
            writer.send(f'write {first + 6}')
            writer.send('checkpoint')
            reader.send(f'release {first + 1}')
            writer.send(f'write {first + 7}')
            writer.send('rollback')
            writer.send('checkpoint')
            reader.send(f'release {first + 2}')
            reader.send(f'verify {first + 7}')
    finally:
        failures = []
        for client in reversed(clients):
            try:
                client.close()
            except Exception as error:
                failures.append(error)
        if failures:
            raise failures[0]
    cold = Client(args.candidate, database)
    try:
        cold.send('verify 31')
    finally:
        cold.close()
    import shutil
    shutil.rmtree(directory)
    print(json.dumps(dict(writer=writer_name, reader=reader_name, epochs=4,
                          generationsValidated=12, finalRevision=31, passed=True)), flush=True)
