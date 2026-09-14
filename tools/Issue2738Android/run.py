#!/usr/bin/env python3
"""Build and execute both LiteDB variants on an already running Android device."""
import argparse
import json
from pathlib import Path
import subprocess
import sys
import time


PACKAGE = 'org.litedb.issue2738'


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--sdk', type=Path, required=True)
    parser.add_argument('--jdk', type=Path, required=True)
    parser.add_argument('--serial', required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[2]
    project = Path(__file__).with_name('Issue2738Android.csproj')
    args.output.mkdir(parents=True, exist_ok=True)
    adb = [str(args.sdk / 'platform-tools/adb'), '-s', args.serial]
    observed = []
    for source in (False, True):
        variant = 'dev' if source else '5.0.21'
        with (args.output / (variant + '-build.log')).open('w') as log:
            subprocess.run(['dotnet', 'build', str(project), '-c', 'Debug',
                '-p:RuntimeIdentifier=android-x64', '-p:TestingEnabled=false',
                '-p:UseProjectReference=' + str(source).lower(),
                '-p:AndroidSdkDirectory=' + str(args.sdk.resolve()),
                '-p:JavaSdkDirectory=' + str(args.jdk.resolve())],
                stdout=log, stderr=subprocess.STDOUT, check=True, timeout=300)
        apk = project.parent / 'bin/Debug/net10.0-android/android-x64/org.litedb.issue2738-Signed.apk'
        if not apk.is_file():
            raise RuntimeError('signed APK missing')
        subprocess.run(adb + ['shell', 'am', 'force-stop', PACKAGE], check=True, timeout=30)
        subprocess.run(adb + ['install', '--no-incremental', '-r', str(apk)], check=True, timeout=60)
        subprocess.run(adb + ['shell', 'pm', 'clear', PACKAGE], check=True, timeout=30)
        subprocess.run(adb + ['shell', 'monkey', '-p', PACKAGE, '1'], check=True, timeout=30)
        deadline = time.monotonic() + 240
        while True:
            probe = subprocess.run(adb + ['shell', 'run-as', PACKAGE, 'cat', 'files/result.json'],
                                   text=True, capture_output=True, timeout=20)
            if probe.returncode == 0:
                try:
                    result = json.loads(probe.stdout)
                    break
                except json.JSONDecodeError:
                    pass  # The activity may still be flushing its completed report.
            if time.monotonic() > deadline:
                raise TimeoutError('Android regression did not produce a complete report')
            time.sleep(1)
        (args.output / (variant + '.json')).write_text(json.dumps(result, indent=2))
        if (not result['android'] or result['cases'] != 12 or result['unrelatedFailures'] != 0 or
                result['build']['UseProjectReference'].lower() != str(source).lower()):
            raise RuntimeError('wrong runtime/build, missing cases, or unrelated failure; inspect the report')
        observed.append(result)
        print(variant, 'Android cases:', result['cases'], 'reported failures:', result['reportedFailures'], flush=True)
        subprocess.run(adb + ['shell', 'am', 'force-stop', PACKAGE], check=True, timeout=30)
    if any(result['reportedFailures'] for result in observed):
        print('BUG_2738_CONFIRMED')
        return 1
    print('VERIFIED_2738: all 24 actual Android fresh-file lifecycle cases passed')
    return 0


if __name__ == '__main__':
    try:
        sys.exit(main())
    except Exception as error:
        print('HARNESS_2738_ERROR:', error, file=sys.stderr)
        sys.exit(2)
