"""Actual x64 net8 default versus x86 net8/CLR4 legacy family admission."""
import argparse
import os
from pathlib import Path
import subprocess
import tempfile
from concurrent.futures import ThreadPoolExecutor


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--x86-dotnet', required=True)
    args = parser.parse_args()
    root = Path('tools/SharedProtocolProbe/bin/Release')
    mapped = ['dotnet', str(root / 'net8.0/SharedProtocolProbe.dll')]
    legacy_targets = [([args.x86_dotnet, str(root / 'net8.0/SharedProtocolProbe.dll')], '8'),
                      ([str(root / 'net481/SharedProtocolProbe.exe')], '4')]
    with tempfile.TemporaryDirectory() as directory, ThreadPoolExecutor() as executor:
        for legacy, major in legacy_targets:
            for legacy_first in [True, False]:
                filename = str(Path(directory) / ('test-' + major + '-' + str(legacy_first) + '.db'))
                holder_cmd = legacy if legacy_first else mapped
                challenger_cmd = mapped if legacy_first else legacy
                holder_args = ['legacy', '32', major] if legacy_first else ['default', '64', '8']
                challenger_args = ['default', '64', '8'] if legacy_first else ['legacy', '32', major]
                holder = subprocess.Popen(holder_cmd + ['hold', filename] + holder_args,
                                          stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
                try:
                    line = executor.submit(holder.stdout.readline).result(timeout=45).strip()
                    assert line == 'ready', (line, holder.stderr.read() if holder.poll() is not None else 'holder not ready')
                    before = Path(filename).read_bytes()
                    result = subprocess.run(challenger_cmd + ['refused', filename] + challenger_args,
                                            capture_output=True, text=True, timeout=45)
                    assert result.returncode == 0 and result.stdout.strip() == 'done', (result.stdout, result.stderr)
                    assert Path(filename).read_bytes() == before
                    holder.stdin.write('\n')
                    holder.stdin.flush()
                    assert holder.wait(timeout=45) == 0, holder.stderr.read()
                    print('PASS', 'CLR' + major, 'legacy-first=' + str(legacy_first), flush=True)
                finally:
                    if holder.poll() is None:
                        holder.kill()
                        holder.wait()


if __name__ == '__main__':
    main()
