"""CI-only checkpoint-policy experiment; six interleaved rounds, no gate rule changes."""
import json
import os
from pathlib import Path
import platform
import statistics
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parent


def run(framework, output):
    if os.environ.get('GITHUB_ACTIONS') != 'true':
        raise RuntimeError('CI only')
    output.mkdir(parents=True, exist_ok=True)
    rounds = json.loads((ROOT / 'gate.json').read_text())['rounds']
    variants = [('v5', 1)] + [(c, k) for k in [1, 2, 4] for c in ['dev-os-cache', 'dev-default']]
    env = dict(os.environ, DOTNET_TieredCompilation='0')
    (output / 'environment.json').write_text(json.dumps(dict(platform=platform.platform(),
        sha=os.environ['GITHUB_SHA'], image=os.environ.get('ImageVersion'), rounds=rounds,
        dotnet=subprocess.check_output(['dotnet', '--info'], text=True)), indent=2))
    for r in range(1, rounds + 1):
        order = variants[r % len(variants):] + variants[:r % len(variants)]
        if r % 2 == 0:
            order.reverse()
        for config, k in order:
            project = 'V5' if config == 'v5' else 'V6'
            dll = ROOT / project / 'bin' / 'Release' / framework / f'{project}.dll'
            dest = output / f'round-{r}-{config}-k{k}.json'
            childenv = dict(env, CHECKPOINT_PAGES=str(1000*k))
            print(f'round {r}: {config} k={k}', flush=True)
            child = subprocess.run(['dotnet', str(dll), config, str(r), str(dest)], env=childenv,
                stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, timeout=480)
            dest.with_suffix('.log').write_text(child.stdout)
            child.check_returncode()
    # Separate instrumented scenarios: never used as gate timings.
    for config, k in [('v5', 1), ('dev-os-cache', 1), ('dev-os-cache', 2), ('dev-os-cache', 4)]:
        dll = ROOT / ('DiagnosticV5' if config == 'v5' else 'Diagnostic') / ('V5.dll' if config == 'v5' else 'V6.dll')
        dest = output / f'diagnostic-{config}-k{k}.json'
        childenv = dict(env, CHECKPOINT_PAGES=str(k*1000), CHECKPOINT_DIAGNOSTICS='1',
            BENCHMARK_SCENARIOS='single_insert_5k_tx,bulk_insert_100k,checkpoint_100k')
        child = subprocess.run(['dotnet', str(dll), config, '1', str(dest)], env=childenv,
            stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, timeout=480)
        dest.with_suffix('.log').write_text(child.stdout)
        child.check_returncode()
    recoveries = []
    for config, k in [('v5', 1), ('dev-os-cache', 1), ('dev-os-cache', 2), ('dev-os-cache', 4)]:
        dll = ROOT / ('V5' if config == 'v5' else 'V6') / 'bin' / 'Release' / framework / ('V5.dll' if config == 'v5' else 'V6.dll')
        childenv = dict(env, CHECKPOINT_PAGES=str(k*1000))
        for r in range(1, rounds+1):
            with tempfile.TemporaryDirectory(prefix='litedb-crash-') as folder:
                path = str(Path(folder) / 'crash.db')
                process = subprocess.Popen(['dotnet', str(dll), '--crash-write', path], env=childenv,
                    stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
                try:
                    import concurrent.futures
                    with concurrent.futures.ThreadPoolExecutor() as pool:
                        ready = pool.submit(process.stdout.readline).result(timeout=90)
                    if not ready.startswith('READY '):
                        raise RuntimeError(f'Crash worker failed: {ready}')
                    _, count, wal = ready.split()
                finally:
                    process.kill()
                    process.wait(timeout=30)
                opens = []
                for attempt in range(2):
                    child = subprocess.run(['dotnet', str(dll), '--recover', path, count], env=childenv,
                        capture_output=True, text=True, timeout=90, check=True)
                    opens.append(json.loads(child.stdout))
                recoveries.append(dict(config=config, k=k, round=r, walBytes=int(wal), **opens[0], repeatOpen=opens[1]))
    (output / 'recovery.json').write_text(json.dumps(recoveries, indent=2))
    summarize(output)


def summarize(output):
    samples = {}
    for file in output.glob('round-*.json'):
        data = json.loads(file.read_text())
        k = int(file.stem[-1])
        for sample in data['samples']:
            samples.setdefault((data['config'], k, sample['scenario']), []).append(sample['milliseconds'])
    results = []
    for (config, k, scenario), values in samples.items():
        if config == 'v5' or scenario.startswith('durable_insert_'):
            continue
        for label, reference in [('v5', samples[('v5', 1, scenario)]), ('v6-1x', samples[(config, 1, scenario)])]:
            results.append(dict(config=config, k=k, scenario=scenario, reference=label,
                ratio=statistics.median(values)/statistics.median(reference),
                lower=min(values)/max(reference), upper=max(values)/min(reference),
                medianMs=statistics.median(values), minMs=min(values), maxMs=max(values)))
    (output / 'ratios.json').write_text(json.dumps(results, indent=2))
    lines = ['|Scenario|Config|k|Reference|Median ratio|Observed bounds|', '|---|---|---:|---|---:|---|']
    for row in results:
        lines.append(f"|{row['scenario']}|{row['config']}|{row['k']}|{row['reference']}|{row['ratio']:.3f}|{row['lower']:.3f}–{row['upper']:.3f}|")
    (output / 'ratios.md').write_text('\n'.join(lines))


if __name__ == '__main__':
    run(sys.argv[1], Path(sys.argv[2]).resolve())
