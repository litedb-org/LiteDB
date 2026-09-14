#!/usr/bin/env python3
"""Repeat the file-backed vector regression in fresh testhost processes."""
import argparse
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import time
import xml.etree.ElementTree as ET
from xml.sax.saxutils import escape


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source-ref', help='Git commit to test; defaults to the working source')
    parser.add_argument('--attempts', type=int, default=100)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    if not 1 <= args.attempts <= 1000:
        parser.error('--attempts must be between 1 and 1000')
    root = Path(__file__).resolve().parents[2]
    args.output.mkdir(parents=True, exist_ok=True)
    output = args.output.resolve()
    with tempfile.TemporaryDirectory(prefix='litedb-2712-') as temporary:
        work = Path(temporary)
        source = root
        commit = subprocess.check_output(['git', 'rev-parse', args.source_ref or 'HEAD'], cwd=root, text=True).strip()
        if args.source_ref:
            archive = work / 'source.tar'
            with archive.open('wb') as target:
                subprocess.run(['git', 'archive', commit], cwd=root, stdout=target, check=True)
            source = work / 'source'
            source.mkdir()
            subprocess.run(['tar', '-xf', str(archive), '-C', str(source)], check=True)
        project = work / 'Probe.csproj'
        project.write_text('''<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>
<TargetFramework>net8.0</TargetFramework><IsTestProject>true</IsTestProject>
<AssemblyName>LiteDB.Tests</AssemblyName><EnableDefaultCompileItems>false</EnableDefaultCompileItems>
<LangVersion>latest</LangVersion></PropertyGroup><ItemGroup>
<PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1"/>
<PackageReference Include="xunit" Version="2.9.2"/>
<PackageReference Include="xunit.runner.visualstudio" Version="2.8.2"/>
<PackageReference Include="FluentAssertions" Version="6.12.1"/>
''' + f'<ProjectReference Include="{escape(str(source / "LiteDB/LiteDB.csproj"))}"/>\n' +
            ''.join(f'<Compile Include="{escape(str(root / file))}"/>\n' for file in (
                'LiteDB.Tests/Issues/Issue2712_Tests.cs', 'LiteDB.Tests/Utils/TempFile.cs')) +
            '</ItemGroup></Project>')
        with (output / 'build.log').open('w') as log:
            subprocess.run(['dotnet', 'build', str(project), '-c', 'Release', '-p:TestingEnabled=true'],
                           stdout=log, stderr=subprocess.STDOUT, check=True, timeout=240)
        results = []
        for attempt in range(1, args.attempts + 1):
            directory = output / str(attempt)
            directory.mkdir(exist_ok=True)
            started = time.monotonic()
            with (directory / 'test.log').open('w') as log:
                run = subprocess.run(['dotnet', 'test', str(project), '-c', 'Release', '--no-build',
                    '--filter', 'FullyQualifiedName~Issue2712_Tests', '--logger', 'trx;LogFileName=result.trx',
                    '--results-directory', str(directory)], stdout=log, stderr=subprocess.STDOUT, timeout=60)
            tree = ET.parse(directory / 'result.trx')
            tests = tree.findall('.//{*}UnitTestResult')
            if len(tests) != 3 or any(test.attrib['outcome'] not in ('Passed', 'Failed') for test in tests):
                raise RuntimeError('all three real-file regression cases must execute')
            failures = [test.findtext('.//{*}Message', '') for test in tests if test.attrib['outcome'] == 'Failed']
            if run.returncode != (1 if failures else 0) or any('Expected mapped.Id to be ' not in error for error in failures):
                raise RuntimeError(f'unrelated failure in attempt {attempt}; inspect the retained TRX')
            results.append({'attempt': attempt, 'seconds': time.monotonic() - started, 'failures': failures})
            report = {'sourceCommit': commit, 'workingSource': not bool(args.source_ref), 'freshProcesses': len(results),
                      'executedCases': len(results) * 3, 'reportedFailures': sum(len(result['failures']) for result in results),
                      'results': results}
            (output / 'summary.json').write_text(json.dumps(report, indent=2))
            print(f"attempt={attempt} reportedFailures={len(failures)}", flush=True)
        print('BUG_2712_CONFIRMED' if report['reportedFailures'] else 'VERIFIED_2712: no failure in these attempts')
        return 1 if report['reportedFailures'] else 0


if __name__ == '__main__':
    try:
        sys.exit(main())
    except Exception as error:
        print('HARNESS_2712_ERROR:', error, file=sys.stderr)
        sys.exit(2)
