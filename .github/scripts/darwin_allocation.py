"""Native bounded-APFS regression: repeated reserve and killed-authority reset.

No synthetic ENOSPC or allocator hook: every reservation calls the product P/Invoke.
"""
import json
import os
from pathlib import Path
import platform
import selectors
import shutil
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / 'artifacts_temp/darwin-allocation'
SIZE = 64 * 1024 * 1024
HARNESS = ROOT / 'LiteDB.Tests.SharedMutexHarness/bin/Release/net8.0/SharedMutexHarness.dll'


def child(mode, path, cut='none', kill=False):
    command = ['dotnet', str(HARNESS), 'mvcc', 'darwin-allocation-' + mode, str(path), '-', cut]
    print('COMMAND', command, flush=True)
    if not kill:
        result = subprocess.run(command, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=90)
        print(result.stdout, flush=True)
        if result.returncode:
            raise RuntimeError(f'{mode} exit={result.returncode}')
        return
    process = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    transcript = b''
    selector = selectors.DefaultSelector()
    selector.register(process.stdout, selectors.EVENT_READ)
    try:
        while True:
            if not selector.select(90):
                raise RuntimeError(f'{mode} never acknowledged {cut}')
            chunk = os.read(process.stdout.fileno(), 65536)
            if not chunk:
                raise RuntimeError(f'{mode} exited before cut: {process.wait()}')
            transcript += chunk
            if b'\nready\n' in transcript:
                process.kill()
                process.wait(timeout=15)
                print(f'KILLED acknowledged cut={cut}', flush=True)
                return
    finally:
        selector.close()
        if process.poll() is None:
            process.kill()
            process.wait(timeout=15)
        print(transcript.decode(errors='replace'), flush=True)


def constrain(mount, allocated, sparse=False):
    physical = allocated.stat().st_blocks * 512
    assert allocated.stat().st_size == SIZE, (allocated, physical)
    assert (0 < physical < SIZE) if sparse else physical >= SIZE, physical
    # Real writes (not truncate/preallocation) consume APFS blocks. Leave enough
    # metadata headroom, but less than one extra complete authority.
    with (mount / 'filler').open('wb', buffering=0) as filler:
        block = os.urandom(1024 * 1024)
        target_free = max(24 * 1024 * 1024, SIZE - physical + 16 * 1024 * 1024)
        while shutil.disk_usage(mount).free > target_free:
            filler.write(block)
        os.fsync(filler.fileno())
    free = shutil.disk_usage(mount).free
    print(f'CONSTRAINED physical={physical} free={free} map={SIZE}', flush=True)
    assert 8 * 1024 * 1024 < free < SIZE


def scenario(kind, mount):
    path = mount / ('direct.bin' if kind == 'direct' else 'test.db')
    if kind.startswith('direct'):
        sparse = kind == 'direct-sparse'
        child('direct-seed', path, 'sparse' if sparse else 'none')
        constrain(mount, path, sparse=sparse)
        child('direct-repeat', path)
        # The inode is disposable coordination, never DB/WAL data. Reservation
        # may discard its contents; it must restore length and physical coverage.
        assert path.stat().st_size == SIZE and path.stat().st_blocks * 512 >= SIZE
    else:
        child('seed', path, 'claimed', kill=True)
        constrain(mount, Path(str(path) + '-shm'))
        # First demonstrate the actual same-size capacity reset independently of
        # the interrupted-retry cases, then force reset again by invalidation.
        child('reset', path)
        for cut in ['invalidated', 'darwin-truncated', 'allocated', 'mapped', 'envelope-b']:
            # Zero only disposable envelopes under quiescence to force reset;
            # retain all physical extents and the unchanged logical length.
            with Path(str(path) + '-shm').open('r+b', buffering=0) as file:
                file.write(bytes(8192))
                os.fsync(file.fileno())
            for repeat in range(2):
                child('reset', path, cut, kill=True)
                # envelope-b is valid on retry: invalidate under quiescence again.
                with Path(str(path) + '-shm').open('r+b', buffering=0) as file:
                    file.write(bytes(8192))
                    os.fsync(file.fileno())
            child('reset', path)
        for repeat in range(2):
            child('reset', path)


def main():
    if platform.system() != 'Darwin':
        raise SystemExit('Native Darwin required')
    OUT.mkdir(parents=True, exist_ok=True)
    report = {'sha': subprocess.check_output(['git', 'rev-parse', 'HEAD'], text=True).strip(),
              'platform': platform.platform(), 'tests': {}}
    for kind in ['direct', 'direct-sparse', 'reset']:
        with tempfile.TemporaryDirectory(prefix='litedb-apfs-') as directory:
            image = Path(directory) / 'bounded.dmg'
            mount = Path(directory) / 'mount'
            mount.mkdir()
            subprocess.run(['hdiutil', 'create', '-size', '256m', '-fs', 'APFS', '-volname', 'LiteDBAllocation', str(image)], check=True)
            subprocess.run(['hdiutil', 'attach', '-nobrowse', '-mountpoint', str(mount), str(image)], check=True)
            try:
                subprocess.run(['diskutil', 'info', str(mount)], check=True)
                scenario(kind, mount)
                report['tests'][kind] = 'passed'
            except Exception as error:
                report['tests'][kind] = str(error)
            finally:
                subprocess.run(['hdiutil', 'detach', str(mount)], check=True)
        (OUT / 'summary.json').write_text(json.dumps(report, indent=2))
    print(json.dumps(report, indent=2), flush=True)
    if any(value != 'passed' for value in report['tests'].values()):
        raise SystemExit(1)


if __name__ == '__main__':
    main()
