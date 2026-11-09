"""Repeat the entire lifetime class; preserve every failure and actual runtime guard."""
import argparse
import json
import os
from pathlib import Path
import re
import subprocess
import xml.etree.ElementTree as ET

parser = argparse.ArgumentParser()
parser.add_argument('--framework', required=True)
parser.add_argument('--repeat', type=int, default=200)
parser.add_argument('--runtime-major', required=True)
args = parser.parse_args()
output = Path('artifacts_temp/warm-lifetime')
output.mkdir(parents=True, exist_ok=True)
config_path = Path(f'LiteDB.Tests/bin/Release/{args.framework}/LiteDB.Tests.runtimeconfig.json')
config = json.loads(config_path.read_text(encoding='utf-8-sig'))
config['runtimeOptions']['framework']['version'] = f'{args.runtime_major}.0.0'
config['runtimeOptions']['rollForward'] = 'LatestPatch'
config_path.write_text(json.dumps(config, indent=2))
os.environ['LITEDB_EXPECTED_RUNTIME_MAJOR'] = args.runtime_major
os.environ['LITEDB_EXPECTED_ARCHITECTURE'] = 'X64'
classes = ['LiteDB.Internals.SharedOneFileWarmLifetime_Tests', 'LiteDB.Tests.Engine.TestHost_Tests']
ns = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
report = dict(sha=os.environ.get('GITHUB_SHA'), framework=args.framework,
              runtimeMajor=args.runtime_major, runs=[], errors=[])
for repeat in range(args.repeat):
    trx = output / f'{repeat}.trx'
    command = ['dotnet', 'vstest', f'LiteDB.Tests/bin/Release/{args.framework}/LiteDB.Tests.dll',
               f'/Framework:.NETCoreApp,Version=v{args.runtime_major}.0', '/Platform:x64',
               '/Settings:tests.runsettings', '/TestCaseFilter:' +
               '|'.join('FullyQualifiedName~' + name + '.' for name in classes),
               f'/Logger:trx;LogFileName={trx.name}', '/ResultsDirectory:' + str(output)]
    with (output / f'{repeat}.log').open('w') as log:
        result = subprocess.run(command, stdout=log, stderr=subprocess.STDOUT, timeout=360)
    try:
        tree = ET.parse(trx)
        rows = tree.findall('.//t:UnitTestResult', ns)
        definitions = {t.attrib['id']: t.find('t:TestMethod', ns).attrib['className'].split(',')[0]
                       for t in tree.findall('.//t:UnitTest', ns)}
        counters = tree.find('.//t:ResultSummary/t:Counters', ns).attrib
        metrics = re.findall(r'WARM_RELEASE[^\r\n]+', '\n'.join(tree.getroot().itertext()))
        report['runs'].append(dict(repeat=repeat, counters=counters, metrics=metrics))
        if (len(rows) != 12 or int(counters['total']) != 12 or
                set(definitions.values()) != set(classes) or len(metrics) != 1 or
                any(row.attrib['outcome'] != 'Passed' for row in rows) or result.returncode):
            raise RuntimeError(f'Failed/missing/skipped results: {counters}, exit={result.returncode}')
    except Exception as error:
        report['errors'].append(f'{repeat}: {error}')
    (output / 'summary.json').write_text(json.dumps(report, indent=2))
    print(f'{repeat + 1}/{args.repeat}: {len(report["errors"])} failures', flush=True)
if len(report['runs']) != args.repeat or report['errors']:
    raise SystemExit('\n'.join(report['errors']) or 'Missing repetitions')
