"""CI-only report-only EventPipe comparison; no product probes or timing verdicts."""
import json
import os
from pathlib import Path
import subprocess
import sys

assert os.environ.get('GITHUB_ACTIONS') == 'true'
framework = sys.argv[1]
out = Path('artifacts_temp/p2-profile').resolve()
out.mkdir(parents=True, exist_ok=True)
identities = {v: subprocess.check_output(['git', '-C', f'artifacts_temp/{v}', 'rev-parse', 'HEAD'], text=True).strip() for v in ['before', 'after']}
(out / 'identity.json').write_text(json.dumps(dict(identities=identities, framework=framework, harness=os.environ['GITHUB_SHA']), indent=2))
for repeat in range(1, 3):
    for variant in (['before', 'after'] if repeat % 2 else ['after', 'before']):
        label = f'{variant}-{repeat}'
        trace = out / f'{label}.nettrace'
        env = dict(os.environ, DOTNET_TieredCompilation='0', LITEDB_PROFILE='1',
                   BENCHMARK_HARNESS_SHA=os.environ['GITHUB_SHA'], BENCHMARK_CANDIDATE_SHA=identities[variant],
                   BENCHMARK_SCENARIOS='bulk_insert_100k,single_insert_5k_tx,update_10k_one_tx,delete_10k_one_tx,full_scan_3x100k')
        dll = Path(f'artifacts_temp/profile-bin-{variant}/V6.dll').resolve()
        command = ['dotnet-trace', 'collect', '--profile', 'cpu-sampling', '--providers',
                   'LiteDB-ScenarioProfile:0xffffffffffffffff:4,Microsoft-Windows-DotNETRuntime:0x4001:5',
                   '--buffersize', '256', '--output', str(trace), '--',
                   'dotnet', str(dll), 'dev-os-cache', str(repeat), str(out / f'{label}.json')]
        with (out / f'{label}.log').open('w') as handle:
            subprocess.run(command, env=env, stdout=handle, stderr=subprocess.STDOUT, check=True, timeout=600)
        subprocess.run(['dotnet', 'tools/Ab5021Benchmarks/TraceReport/bin/Release/net8.0/TraceReport.dll', str(trace), str(out / f'{label}-frames.json')], check=True, timeout=300)
        rows = json.loads((out / f'{label}-frames.json').read_text())
        assert {r['scenario'] for r in rows} == set(env['BENCHMARK_SCENARIOS'].split(','))
        assert all(r['samples'] > 0 for r in rows)
