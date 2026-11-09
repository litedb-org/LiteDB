"""Bounded target/seed legs, with child shutdown before evidence collection.

Ported from the b811372fb external campaign driver. Full seed budgets replace
interrupted input prefixes; successful runs are replayed from recorded input.
"""
import argparse
import hashlib
import json
import pathlib
import subprocess
import time


def shutdown(binary, root):
    import psutil
    binary = str(binary.resolve())
    processes = []
    for process in psutil.process_iter(['pid', 'cmdline']):
        try:
            if binary in (process.info['cmdline'] or []):
                processes.append(process)
        except (psutil.NoSuchProcess, psutil.AccessDenied):
            continue
    # Snapshot readers own these files after publication. Ask them to release
    # their cursors before terminating any orphan writer/runner processes.
    for ready in root.rglob('reader-*.ready'):
        stop = ready.with_suffix('.stop')
        temporary = stop.with_suffix('.stop.publishing')
        temporary.write_text('stop')
        temporary.replace(stop)
    _, alive = psutil.wait_procs(processes, timeout=10)
    for process in alive:
        try:
            process.terminate()
        except psutil.NoSuchProcess:
            pass
    _, alive = psutil.wait_procs(alive, timeout=5)
    for process in alive:
        try:
            process.kill()
        except psutil.NoSuchProcess:
            pass
    _, alive = psutil.wait_procs(alive, timeout=5)
    if alive:
        raise RuntimeError('Campaign processes remain alive; evidence is unstable')


def validate_results(root, target, seed, sha):
    results = [json.loads(p.read_text()) for p in root.rglob('run.json')]
    selected = [r for r in results if r['target'] == target and r['seed'] == seed and r['count'] == 400]
    if len(selected) < 2:
        raise RuntimeError('Missing full-budget original/recorded-input replay')
    for result in selected:
        if result['status'] != 'passed' or result['steps'] != 400 or result['gitSha'] != sha:
            raise RuntimeError('Incomplete or failed campaign: ' + json.dumps(result))
    if len({(r['inputHash'], r['traceHash']) for r in selected}) != 1:
        raise RuntimeError('Seed/replay input or trace differs')
    return selected


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--target', choices=['api-boundary', 'snapshot'])
    parser.add_argument('--seed', type=int)
    parser.add_argument('--shutdown-only', action='store_true')
    args = parser.parse_args()
    root = pathlib.Path('artifacts/harness')
    root.mkdir(parents=True, exist_ok=True)
    binary = pathlib.Path('LiteDB.Fuzz/bin/Release/net8.0/LiteDB.Fuzz.dll').resolve()
    if args.shutdown_only:
        shutdown(binary, root)
        return
    if args.target is None or args.seed is None:
        parser.error('--target and --seed required')
    sha = subprocess.check_output(['git', 'rev-parse', 'HEAD'], text=True).strip()
    command = ['dotnet', str(binary), '--target', args.target, '--seed', str(args.seed),
               '--count', '400', '--determinism-check', '--max-artifact-mb', '0',
               '--artifact-dir', str(root)]
    record = dict(command=command, sha=sha)
    (root / 'command.json').write_text(json.dumps(record, indent=2))
    try:
        with (root / 'campaign.log').open('w', encoding='utf-8') as log:
            process = subprocess.Popen(command, stdout=log, stderr=subprocess.STDOUT)
            try:
                record['exit'] = process.wait(timeout=150 * 60)
            except subprocess.TimeoutExpired:
                record['timeout'] = True
                raise
        if record['exit']:
            raise RuntimeError('Fuzz runner failed; see campaign.log')
        results = validate_results(root, args.target, args.seed, sha)
        (root / 'validated.json').write_text(json.dumps(results, indent=2))
    finally:
        shutdown(binary, root)
        (root / 'command.json').write_text(json.dumps(record, indent=2))
        hashes = {str(p): hashlib.sha256(p.read_bytes()).hexdigest()
                  for p in binary.parent.iterdir() if p.is_file()}
        (root / 'binary-sha256.json').write_text(json.dumps(hashes, indent=2))


if __name__ == '__main__':
    main()
