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
           for separate in ('', '_separate') for checkpoint in ('', '_nocp') for n in (1, 4, 8)]
metadata = dict(sha=os.environ['GITHUB_SHA'], candidateSha=os.environ.get('BENCHMARK_CANDIDATE_SHA'), framework=framework,
                dotnet=subprocess.check_output(['dotnet', '--info'], text=True),
                note='v5 writers use OS-cache commits; v6 writers use device-sync commits. Not durability equivalents.')
(out / 'environment.json').write_text(json.dumps(metadata, indent=2))
for version, config, scenarios in [('V6', 'dev-default', writers)]:
    trace = out / f'{config}.nettrace'
    env = dict(os.environ, DOTNET_TieredCompilation='0', LITEDB_PROFILE='1',
               BENCHMARK_SCENARIOS=','.join(scenarios))
    dll = root / version / 'bin' / 'Release' / framework / f'{version}.dll'
    command = ['dotnet', str(dll), config, '1', str(out / f'{config}.json')]
    with (out / f'{config}.log').open('w') as handle:
        subprocess.run(command, env=env, stdout=handle, stderr=subprocess.STDOUT, check=True, timeout=1500)
    rows = json.loads((out / f'{config}.json.counters.json').read_text())
    if {row['scenario'] for row in rows} != set(writers) or len(rows) != len(writers):
        raise RuntimeError('Missing/duplicate counter scenarios')
    for row in rows:
        if (row['commits'] != 5000 or row['appends'] != 5000 or
            row['groups'] + row['piggybacks'] != 5000 or
            not row['groups'] == row['starts'] == row['ends'] == row['native']):
            raise RuntimeError(f'Incomplete counters: {row}')
