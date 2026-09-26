#!/usr/bin/env python3
"""Production state campaign: snapshots, conflicting writers, rollback, death and raw validation."""
import argparse
import json
import os
from pathlib import Path
import queue
import shutil
import subprocess
import tempfile
import threading
import time

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--baseline', type=Path, required=True)
parser.add_argument('--candidate', type=Path, required=True)
parser.add_argument('--validator', type=Path, required=True, help='Hook-enabled LiteDB.Fuzz.dll, used only after quiescence')
parser.add_argument('--scratch', type=Path, required=True)
args = parser.parse_args()
args.scratch.mkdir(parents=True, exist_ok=True, mode=0o700)
env = dict(os.environ, TMPDIR=str(args.scratch.resolve()))

class Client:
    def __init__(self, runner, database):
        self.errors = (database.parent / f'child-{len(list(database.parent.glob("child-*.stderr.log")))}.stderr.log').open('w+t')
        self.child = subprocess.Popen(['dotnet', str(runner.resolve() / 'SharedReadBenchmarks.dll'),
                                       'interop', str(database)], stdin=subprocess.PIPE,
                                      stdout=subprocess.PIPE, stderr=self.errors, text=True, env=env)
        self.lines = queue.Queue(maxsize=8)
        self.closed = False
        self.reader = threading.Thread(target=self.read, daemon=True)
        self.reader.start()

    def read(self):
        for line in self.child.stdout:
            self.lines.put(line.strip())
        self.lines.put(None)

    def begin(self, command):
        self.child.stdin.write(command + '\n')
        self.child.stdin.flush()

    def finish(self, command):
        try:
            answer = self.lines.get(timeout=max(0.01, min(30, deadline - time.monotonic())))
        except queue.Empty:
            raise RuntimeError('Timed out: ' + command)
        if answer != command:
            self.errors.seek(0)
            raise RuntimeError(f'{command}: {answer}: {self.errors.read()}')

    def send(self, command):
        self.begin(command)
        self.finish(command)

    def close(self, kill=False):
        if self.closed:
            return
        self.closed = True
        try:
            if self.child.poll() is None:
                if kill:
                    self.child.kill()
                else:
                    self.child.stdin.write('exit\n')
                    self.child.stdin.flush()
                self.child.wait(timeout=10)
        finally:
            if self.child.poll() is None:
                self.child.kill()
                self.child.wait(timeout=5)
            self.reader.join(timeout=5)
            if self.reader.is_alive():
                raise RuntimeError('Output reader did not stop')
            self.errors.close()
        if self.child.returncode and not kill:
            raise RuntimeError('Child failed: ' + str(self.child.returncode))

# Concurrent participants use exactly the same library version. Compare each
# production variant on its own database, never across a mixed-version history.
for writer_name, reader_name in [('baseline', 'baseline'), ('candidate', 'candidate')]:
    directory = Path(tempfile.mkdtemp(prefix='interop-', dir=args.scratch.resolve()))
    database = directory / 'test.db'
    clients = []
    deadline = time.monotonic() + 140
    try:
        writer = Client(getattr(args, writer_name), database)
        clients.append(writer)
        writer.send('write 0')
        reader = Client(getattr(args, reader_name), database)
        peer = Client(getattr(args, reader_name), database)
        clients.extend([reader, peer])
        for epoch in range(4):
            first = epoch * 8
            writer.send(f'write {first}')
            reader.send(f'open {first}')
            writer.send(f'write {first + 1}')
            reader.send(f'open {first + 1}')
            writer.send(f'write {first + 2}')
            reader.send(f'open {first + 2}')
            doomed = Client(getattr(args, reader_name), database)
            clients.append(doomed)
            doomed.send(f'open {first + 2}')
            # Both explicit transactions replace the same documents and witness.
            # Either complete history is legal; mixed payloads or witnesses are not.
            writer.begin(f'write {first + 3}')
            peer.begin(f'write {first + 4}')
            writer.finish(f'write {first + 3}')
            peer.finish(f'write {first + 4}')
            peer.send(f'verify-either {first + 3} {first + 4}')
            doomed.close(kill=True)
            writer.send(f'write {first + 5}')
            writer.send('checkpoint')
            writer.send('uncommitted')
            writer.close(kill=True)
            # Preserve the unrecovered files before allowing any owner to reopen them.
            recovery = directory / f'pre-recovery-{epoch}'
            recovery.mkdir()
            for path in directory.glob('test*.db'):
                shutil.copy2(path, recovery / path.name)
            peer.send(f'verify {first + 5}')
            writer = Client(getattr(args, writer_name), database)
            clients.append(writer)
            reader.send(f'release {first}')
            writer.send(f'write {first + 6}')
            writer.send('checkpoint')
            reader.send(f'release {first + 1}')
            writer.send(f'write {first + 7}')
            writer.send('rollback')
            writer.send('checkpoint')
            reader.send(f'release {first + 2}')
            reader.send(f'verify {first + 7}')
            shutil.rmtree(recovery)
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
    validation = subprocess.run(['dotnet', str(args.validator.resolve()), '--child', 'verify-checkpointed',
                                 '--database', str(database), '--artifact-dir', str(directory/'integrity')],
                                text=True, capture_output=True, env=env, check=True, timeout=30)
    metrics = json.loads(validation.stdout)
    shutil.rmtree(directory)
    print(json.dumps(dict(writer=writer_name, reader=reader_name, epochs=4, conflictingTransactions=8,
                          killedUncommittedWriters=4, killedReaders=4, rollbacks=4, generationsValidated=12,
                          finalRevision=31, integrity=metrics, passed=True)), flush=True)
