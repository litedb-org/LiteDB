"""Audit all six hosted legs and retain per-repetition medians and sample bounds."""
import hashlib
import itertools
import json
from pathlib import Path
import statistics
import sys

CANDIDATE = "7000dd6f01d0187a8fb5d030064eb327864ab731"
HARNESS = sys.argv[3]
root = Path(sys.argv[1])
out = Path(sys.argv[2])
out.mkdir(parents=True, exist_ok=True)
files = sorted(root.glob("**/all.json"))
assert len(files) == 6, f"expected six legs, found {len(files)}"
summary = []
identities = set()
manifest = {}
for file in files:
    rows = json.loads(file.read_text())
    keys = {(r['repetition'], r['round'], r['scenario'], r['workers'], r['variant']) for r in rows}
    expected = set(itertools.product((1, 2), range(6), ('read', 'insert', 'mixed'), (1, 4), ('v5', 'v6')))
    assert len(rows) == len(keys) == 144 and keys == expected, file
    first = rows[0]['processes'][0]
    identity = (first['os'], first['architecture'], first['framework'])
    assert identity not in identities, identity
    identities.add(identity)
    assert first['framework'] in ('.NETCoreApp,Version=v8.0', '.NETCoreApp,Version=v10.0')
    assert any(name in first['os'] for name in ('Windows', 'Linux', 'Darwin'))
    os_name = 'Windows x64' if 'Windows' in first['os'] else 'macOS ARM64' if first['architecture'] == 'Arm64' else 'Linux x64'
    assert first['architecture'] == ('Arm64' if os_name == 'macOS ARM64' else 'X64')
    binaries = {'v5': set(), 'v6': set()}
    for row in rows:
        assert row['verified'] and row['requestsPerWorker'] == 20 and row['warmupRequests'] == 10
        assert len(row['processes']) == row['workers'] and row['latencyMs'] > 0 and row['wallMsPerRequest'] > 0
        for process in row['processes']:
            assert (process['os'], process['architecture'], process['framework']) == identity
            assert process['candidateCommit'] == CANDIDATE and process['harnessCommit'] == HARNESS
            assert process['tieredCompilation'] == '0' and process['requests'] == 20
            version = '8' if 'v8.0' in process['framework'] else '10'
            assert process['runtime'].startswith(f'.NET {version}.')
            assert process['milliseconds'] > 0
            binaries[row['variant']].add(process['assemblySha256'])
    assert all(len(hashes) == 1 for hashes in binaries.values())
    for repetition, scenario, workers, metric in itertools.product((1, 2), ('read', 'insert', 'mixed'), (1, 4), ('latencyMs', 'wallMsPerRequest')):
        samples = {v: [r[metric] for r in rows if (r['repetition'], r['scenario'], r['workers'], r['variant']) == (repetition, scenario, workers, v)] for v in ('v5', 'v6')}
        stats = {v: dict(median=statistics.median(values), low=min(values), high=max(values)) for v, values in samples.items()}
        summary.append(dict(platform=os_name, framework=first['framework'], repetition=repetition,
                            scenario=scenario, workers=workers, metric=metric, **stats,
                            ratio=stats['v6']['median']/stats['v5']['median'],
                            ratioLow=stats['v6']['low']/stats['v5']['high'],
                            ratioHigh=stats['v6']['high']/stats['v5']['low']))
    manifest[str(file.relative_to(root))] = hashlib.sha256(file.read_bytes()).hexdigest()
(out / 'summary.json').write_text(json.dumps(summary, indent=2) + '\n')
(out / 'artifact-sha256.json').write_text(json.dumps(manifest, indent=2) + '\n')
lines = ['| OS | Runtime | Rep | Processes | Scenario | v5 ms [min,max] | v6 ms [min,max] | v6/v5 [bounds] |',
         '| --- | --- | ---: | ---: | --- | ---: | ---: | ---: |']
for r in sorted(summary, key=lambda r: (r['platform'], r['framework'], r['repetition'], r['workers'], r['scenario'])):
    if r['metric'] != 'latencyMs':
        continue
    def cell(v):
        return f"{v['median']:.4f} [{v['low']:.4f},{v['high']:.4f}]"
    lines.append(f"| {r['platform']} | {r['framework']} | {r['repetition']} | {r['workers']} | {r['scenario']} | {cell(r['v5'])} | {cell(r['v6'])} | {r['ratio']:.2f}x [{r['ratioLow']:.2f},{r['ratioHigh']:.2f}] |")
(out / 'results.md').write_text('\n'.join(lines) + '\n')
print('Audited six legs, 864 samples, two repetitions, all results verified.')
