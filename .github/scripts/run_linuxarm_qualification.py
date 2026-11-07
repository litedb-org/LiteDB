"""Hosted ARM64 mapped qualification: retain every TRX and reject incomplete execution."""
import argparse
from collections import Counter
import json
import os
from pathlib import Path
import re
import subprocess
import xml.etree.ElementTree as ET

NS = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}


def classes(root, stress=False):
    selected = set()
    for path in (root / 'LiteDB.Tests').rglob('*.cs'):
        source = path.read_text(encoding='utf-8-sig')
        if not (re.search(r'\[(?:OneFile|LinuxFutex)(?:Fact|Theory)', source)
                or path.stem.startswith(('SharedOneFile', 'SharedWriterTenure'))
                or path.stem in ('SharedIntegrationSeam_Tests', 'SharedPlatformWake_Tests')):
            continue
        namespace = re.search(r'namespace\s+([\w.]+)', source).group(1)
        for name in re.findall(r'public\s+(?:sealed\s+)?class\s+(\w+_Tests)\b', source):
            if not stress or name.startswith('SharedWriterTenure'):
                selected.add(namespace + '.' + name)
    if not selected:
        raise ValueError('No qualification classes found')
    return sorted(selected)


def verify(path, expected):
    tree = ET.parse(path)
    definitions = {}
    for test in tree.findall('.//t:UnitTest', NS):
        method = test.find('t:TestMethod', NS)
        definitions[test.attrib['id']] = method.attrib['className'] + '.' + method.attrib['name']
    results = tree.findall('.//t:UnitTestResult', NS)
    counts = Counter()
    actual = set()
    for result in results:
        if result.attrib['outcome'] != 'Passed':
            raise ValueError(f'{path}: {result.attrib["testName"]}: {result.attrib["outcome"]}')
        name = definitions[result.attrib['testId']]
        actual.add(name)
        counts[name.rsplit('.', 1)[0]] += 1
    if not results or actual != expected:
        raise ValueError(f'{path}: missing={sorted(expected-actual)}, unexpected={sorted(actual-expected)}')
    summary = tree.find('.//t:ResultSummary', NS)
    if summary is None or summary.attrib['outcome'] != 'Completed':
        raise ValueError(f'{path}: session did not complete')
    counters = summary.find('t:Counters', NS).attrib
    if int(counters['total']) != len(results) or int(counters['executed']) != len(results):
        raise ValueError(f'{path}: incomplete counters {counters}')
    return counts


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--framework', choices=['net8.0', 'net10.0'], required=True)
    parser.add_argument('--mode', choices=['full', 'stress'], required=True)
    args = parser.parse_args()
    if os.environ.get('GITHUB_ACTIONS') != 'true':
        raise SystemExit('Hosted qualification only; do not run full matrices locally')
    root = Path.cwd()
    output = root / 'artifacts_temp/linuxarm'
    output.mkdir(parents=True, exist_ok=True)
    major = args.framework[3:].split('.')[0]
    os.environ['LITEDB_EXPECTED_RUNTIME_MAJOR'] = major
    os.environ['LITEDB_EXPECTED_ARCHITECTURE'] = 'arm64'
    os.environ['DOTNET_ROLL_FORWARD'] = 'Disable'
    assembly = root / f'LiteDB.Tests/bin/Release/{args.framework}/LiteDB.Tests.dll'
    base = ['dotnet', 'vstest', str(assembly), f'/Framework:.NETCoreApp,Version=v{major}.0',
            '/Platform:arm64']
    listing = output / 'discovered.txt'
    subprocess.run(base + ['/ListFullyQualifiedTests', f'/ListTestsTargetPath:{listing}'], check=True)
    discovered = set(listing.read_text().splitlines())
    selected = classes(root, args.mode == 'stress')
    guards = {name for name in discovered if '.TestHost_Tests.' in name}
    if len(guards) != 2:
        raise ValueError(f'Missing runtime/test-hook guards: {guards}')
    expected = {cls: {name for name in discovered if name.rsplit('.', 1)[0] == cls}
                for cls in selected}
    if any(not methods for methods in expected.values()):
        raise ValueError(f'Undiscovered qualification class: {expected}')
    (output / 'manifest.json').write_text(json.dumps(expected, default=sorted, indent=2))
    totals = Counter()
    repeats = 50 if args.mode == 'stress' else 1
    for repeat in range(1, repeats + 1):
        # Separate sessions retain the existing 300-second safety ceiling.
        for cls in selected:
            name = f'{repeat:02d}-{cls}.trx'
            subprocess.run(base + [f'/TestCaseFilter:FullyQualifiedName~{cls}.|FullyQualifiedName~TestHost_Tests',
                                   f'/Settings:{root / "tests.runsettings"}',
                                   f'/ResultsDirectory:{output}', f'/Logger:trx;LogFileName={name}',
                                   '/Logger:console;verbosity=minimal',
                                   '/Blame:CollectHangDump;TestTimeout=120s;HangDumpType=mini'], check=True)
            counts = verify(output / name, expected[cls] | guards)
            totals.update({key: value for key, value in counts.items() if key == cls})
            summary = dict(sha=os.environ['GITHUB_SHA'], framework=args.framework,
                           mode=args.mode, repeat=repeat, classes=dict(sorted(totals.items())), skipped=0)
            (output / 'counts.json').write_text(json.dumps(summary, indent=2))
            print(f'{repeat}/{repeats} {cls}: {counts[cls]} passed, zero skipped', flush=True)


if __name__ == '__main__':
    main()
