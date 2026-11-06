"""CI-only diagnostic pass. Trace overhead means these timings are not gate measurements."""
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
cpu = []
writers = [f'durable_insert_5k_{n}_writers{separate}{checkpoint}'
           for separate in ('', '_separate') for checkpoint in ('', '_nocp') for n in (4, 8)]
metadata = dict(sha=os.environ['GITHUB_SHA'], framework=framework,
                dotnet=subprocess.check_output(['dotnet', '--info'], text=True),
                note='v5 writers use OS-cache commits; v6 writers use device-sync commits. Not durability equivalents.')
(out / 'environment.json').write_text(json.dumps(metadata, indent=2))
for version, config, scenarios in [('V6', 'dev-default', writers)]:
    trace = out / f'{config}.nettrace'
    env = dict(os.environ, DOTNET_TieredCompilation='0', LITEDB_PROFILE='1',
               BENCHMARK_SCENARIOS=','.join(scenarios))
    dll = root / version / 'bin' / 'Release' / framework / f'{version}.dll'
    command = ['dotnet-trace', 'collect', '--profile', 'cpu-sampling', '--providers',
               'LiteDB-ScenarioProfile:0xffffffffffffffff:4,LiteDB-CommitProfile:0xffffffffffffffff:4',
               '--buffersize', '512', '--output', str(trace), '--',
               'dotnet', str(dll), config, '1', str(out / f'{config}.json')]
    with (out / f'{config}.log').open('w') as handle:
        subprocess.run(command, env=env, stdout=handle, stderr=subprocess.STDOUT, check=True, timeout=1500)
    subprocess.run(['dotnet-trace', 'convert', str(trace), '--format', 'Speedscope'], check=True, timeout=300)
    subprocess.run(['dotnet', str(root / 'TraceReport/bin/Release/net8.0/TraceReport.dll'),
                    str(trace), str(out / f'{config}-frames.json')], check=True, timeout=600)
