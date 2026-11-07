"""Execute every mapped class in isolated, bounded sessions and audit its TRX."""
import argparse
import json
import os
from pathlib import Path
import re
import subprocess
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
NS = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}


def classes(stress=False):
    selected = set()
    for path in (ROOT / 'LiteDB.Tests').rglob('*.cs'):
        source = path.read_text(encoding='utf-8-sig')
        if not re.search(r'\[OneFile(?:Fact|Theory)\b|public class SharedOneFile\w*_Tests', source):
            continue
        namespace = re.search(r'namespace\s+([\w.]+)', source).group(1)
        for name in re.findall(r'public class (\w+_Tests)', source):
            if not stress or 'SharedWriterTenure' in name or 'Process' in name:
                selected.add(namespace + '.' + name)
    selected.add('LiteDB.Internals.SharedPlatformWake_Tests')
    if not stress:
        selected.add('LiteDB.Tests.Engine.SharedIntegrationSeam_Tests')
        selected.add('LiteDB.Tests.Engine.TestHost_Tests')
    return sorted(selected)


def audit(path, expected):
    tree = ET.parse(path)
    definitions = {t.attrib['id']: t.find('t:TestMethod', NS).attrib['className'].split(',')[0]
                   for t in tree.findall('.//t:UnitTest', NS)}
    rows = tree.findall('.//t:UnitTestResult', NS)
    counts = {}
    for row in rows:
        name = definitions[row.attrib['testId']]
        entry = counts.setdefault(name, {'executed': 0, 'skipped': 0, 'failed': 0})
        outcome = row.attrib['outcome']
        entry['executed' if outcome == 'Passed' else 'skipped' if outcome == 'NotExecuted' else 'failed'] += 1
    counters = tree.find('.//t:ResultSummary/t:Counters', NS).attrib
    if set(counts) != set(expected) or not rows or int(counters['total']) != len(rows):
        raise RuntimeError(f'Missing/unexpected/truncated class results: {path}: {counts}, {counters}')
    if any(c['skipped'] or c['failed'] for c in counts.values()):
        raise RuntimeError(f'Non-passing cases: {path}: {counts}')
    return counts


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--framework', required=True)
    parser.add_argument('--shard', type=int, default=0)
    parser.add_argument('--shards', type=int, default=1)
    parser.add_argument('--stress', action='store_true')
    parser.add_argument('--repeat', type=int, default=1)
    args = parser.parse_args()
    output = ROOT / 'artifacts_temp/windows-qualification'
    output.mkdir(parents=True, exist_ok=True)
    os.environ['LITEDB_EXPECTED_RUNTIME_MAJOR'] = args.framework.removeprefix('net').split('.')[0]
    os.environ['LITEDB_EXPECTED_ARCHITECTURE'] = 'X64'
    selected = classes(args.stress)[args.shard::args.shards]
    report = {'sha': os.environ.get('GITHUB_SHA'), 'framework': args.framework,
              'classes': selected, 'runs': [], 'errors': []}
    for repeat in range(args.repeat):
        for name in selected:
            trx = output / f'{name}-{repeat}.trx'
            command = ['dotnet', 'test', 'LiteDB.Tests/LiteDB.Tests.csproj', '-c', 'Release',
                       '-f', args.framework, '--no-build', '-p:TestingEnabled=true',
                       '--settings', 'tests.runsettings', '--filter', f'FullyQualifiedName~{name}.',
                       '--logger', f'trx;LogFileName={trx.name}', '--results-directory', str(output)]
            result = subprocess.run(command, cwd=ROOT)
            try:
                counts = audit(trx, [name])
                if result.returncode:
                    raise RuntimeError(f'Test process failed: {result.returncode}')
                report['runs'].append({'repeat': repeat, 'trx': trx.name, 'counts': counts})
            except Exception as error:
                report['errors'].append(str(error))
            (output / 'summary.json').write_text(json.dumps(report, indent=2))
    if report['errors'] or not selected:
        raise SystemExit('\n'.join(report['errors']) or 'Empty shard')


if __name__ == '__main__':
    main()
