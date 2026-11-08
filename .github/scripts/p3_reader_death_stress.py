"""Repeat complete reader/native-lock classes; audit raw TRX and death timings."""
import argparse
import json
import os
from pathlib import Path
import re
import subprocess
import xml.etree.ElementTree as ET
from p3_windows_qualification import audit

parser = argparse.ArgumentParser()
parser.add_argument('--framework', required=True)
parser.add_argument('--repeat', type=int, default=100)
parser.add_argument('--runtime-major', required=True)
args = parser.parse_args()
output = Path('artifacts_temp/reader-death')
output.mkdir(parents=True, exist_ok=True)
# VSTest /Framework alone does not override an installed net8 runtime.
# Pin the testhost configuration; child processes use that same actual runtime.
config_path = Path(f'LiteDB.Tests/bin/Release/{args.framework}/LiteDB.Tests.runtimeconfig.json')
config = json.loads(config_path.read_text(encoding='utf-8-sig'))
config['runtimeOptions']['framework']['version'] = f'{args.runtime_major}.0.0'
config['runtimeOptions']['rollForward'] = 'LatestPatch'
config_path.write_text(json.dumps(config, indent=2))
classes = ['LiteDB.Internals.SharedOneFileReaders_Tests',
           'LiteDB.Internals.SharedOneFileLock_Tests', 'LiteDB.Tests.Engine.TestHost_Tests']
os.environ['LITEDB_EXPECTED_RUNTIME_MAJOR'] = args.runtime_major
os.environ['LITEDB_EXPECTED_ARCHITECTURE'] = 'X64'
report = dict(sha=os.environ.get('GITHUB_SHA'), framework=args.framework, runtimeMajor=args.runtime_major,
              runner=os.environ.get('RUNNER_OS'), runs=[], errors=[])
for repeat in range(args.repeat):
    trx = output / f'{repeat}.trx'
    command = ['dotnet', 'vstest', f'LiteDB.Tests/bin/Release/{args.framework}/LiteDB.Tests.dll',
               f'/Framework:.NETCoreApp,Version=v{args.runtime_major}.0', '/Platform:x64',
               '/Settings:tests.runsettings', '/TestCaseFilter:' +
               '|'.join('FullyQualifiedName~' + name + '.' for name in classes),
               f'/Logger:trx;LogFileName={trx.name}', '/ResultsDirectory:' + str(output)]
    with (output / f'{repeat}.log').open('w') as log:
        result = subprocess.run(command, stdout=log, stderr=subprocess.STDOUT)
    try:
        counts = audit(trx, classes)
        if result.returncode:
            raise RuntimeError(f'Test process failed: {result.returncode}')
        root = ET.parse(trx)
        content = '\n'.join(root.getroot().itertext())
        metrics = re.findall(r'(READER_DEATH|LOCK_DEATH) kill_to_clear_ms=([\d.]+)[^\r\n]*', content)
        if sorted(m[0] for m in metrics) != ['LOCK_DEATH', 'READER_DEATH']:
            raise RuntimeError('Missing/duplicate death metrics')
        report['runs'].append(dict(repeat=repeat, counts=counts, metrics=metrics))
    except Exception as error:
        report['errors'].append(f'{repeat}: {error}')
    (output / 'summary.json').write_text(json.dumps(report, indent=2))
    print(f'{repeat + 1}/{args.repeat}: {len(report["errors"])} failures', flush=True)
if len(report['runs']) != args.repeat or report['errors']:
    raise SystemExit('\n'.join(report['errors']) or 'Missing repetitions')
