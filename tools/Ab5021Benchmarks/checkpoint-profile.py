"""Report-only checkpoint diagnostics; never gate timings. CI only."""
import json
import os
from pathlib import Path
import subprocess
import sys

if os.environ.get('GITHUB_ACTIONS') != 'true':
    raise RuntimeError('Profiling runs only on GitHub Actions')
framework, destination = sys.argv[1:]
out = Path(destination).resolve()
out.mkdir(parents=True, exist_ok=True)
root = Path(__file__).resolve().parent
(out / 'environment.json').write_text(json.dumps(dict(
    sha=os.environ['GITHUB_SHA'], framework=framework,
    dotnet=subprocess.check_output(['dotnet', '--info'], text=True),
    note='PROFILE probes; v5 exact NuGet binary with observation IL probes. v5 commits are OS cache only; v6 default is durable.'), indent=2))
for version, config in [('V5', 'v5'), ('V6', 'dev-os-cache'), ('V6', 'dev-default')]:
    for repeat in range(1, 4):
        label = f'{config}-{repeat}'
        trace = out / f'{label}.nettrace'
        env = dict(os.environ, DOTNET_TieredCompilation='0', LITEDB_PROFILE='1',
                   BENCHMARK_HARNESS_SHA=os.environ['GITHUB_SHA'], BENCHMARK_CANDIDATE_SHA=os.environ['GITHUB_SHA'],
                   BENCHMARK_SCENARIOS='bulk_insert_100k,update_10k_one_tx,delete_10k_one_tx,single_insert_5k_tx')
        dll = root / version / 'bin' / 'Release' / framework / f'{version}.dll'
        command = ['dotnet-trace', 'collect', '--profile', 'cpu-sampling', '--providers',
                   'LiteDB-ScenarioProfile:0xffffffffffffffff:4,LiteDB-CheckpointProfile:0xffffffffffffffff:4',
                   '--buffersize', '256', '--output', str(trace), '--',
                   'dotnet', str(dll), config, str(repeat), str(out / f'{label}.json')]
        with (out / f'{label}.log').open('w') as handle:
            subprocess.run(command, env=env, stdout=handle, stderr=subprocess.STDOUT, check=True, timeout=1500)
        subprocess.run(['dotnet', str(root / 'TraceReport/bin/Release/net8.0/TraceReport.dll'),
                        str(trace), str(out / f'{label}-frames.json')], check=True, timeout=600)
        rows = json.loads((out / f'{label}-frames.json').read_text())
        measured = rows[-4:]
        assert {r['scenario'] for r in measured} == set(env['BENCHMARK_SCENARIOS'].split(','))
        # Probe presence and page accounting are mandatory; a missing EventSource cannot look like zero work.
        assert sum(sum(r['attempts'].values()) for r in rows) > 0
        assert sum(r['dataWritten'] for r in rows) > 0
