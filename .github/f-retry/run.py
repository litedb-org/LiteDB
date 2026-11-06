"""Alternate exact same-runner child-process xUnit cases, retaining every result."""
import os, sys, subprocess, json
import xml.etree.ElementTree as ET
from pathlib import Path
host, tools, iterations = sys.argv[1], sys.argv[2], int(sys.argv[3])
root = Path.cwd()
evidence = root / 'evidence'
evidence.mkdir(exist_ok=True)
failed = False
for iteration in range(iterations):
    for revision in (['phase2', 'dev'] if iteration % 2 == 0 else ['dev', 'phase2']):
        output = evidence / revision / str(iteration)
        output.mkdir(parents=True)
        env = dict(os.environ, F_RETRY_EVIDENCE=str(output), F_RETRY_TOOLS=tools)
        command = [host, 'vstest', str(root / revision / 'LiteDB.Tests/bin/Release/net8.0/LiteDB.Tests.dll'),
                   '/TestCaseFilter:FullyQualifiedName~SharedRetryWriterRelease_Tests.Child_process_can_write_after_retry_commit',
                   '/Platform:' + sys.argv[4], '/Logger:trx;LogFileName=results.trx', '/ResultsDirectory:' + str(output),
                   '/Settings:' + str(root / revision / 'tests.runsettings')]
        print(json.dumps(dict(iteration=iteration, revision=revision, command=command)), flush=True)
        with (output / 'test.log').open('w') as log:
            result = subprocess.run(command, env=env, stdout=log, stderr=subprocess.STDOUT)
        print(f'{revision} iteration={iteration} exit={result.returncode}', flush=True)
        if result.returncode:
            failed = True
        trx = output / 'results.trx'
        counters = ET.parse(trx).find('.//{*}Counters') if trx.exists() else None
        children = list(output.glob('child-*.txt'))
        if counters is None or counters.get('total') != '4' or len(children) != 4:
            print(f'INVALID EVIDENCE: expected four executed child cases in {output}', flush=True)
            failed = True
        for child in children:
            if f'arch {sys.argv[4].upper()} ' not in child.read_text():
                print(f'INVALID ARCHITECTURE: {child}', flush=True)
                failed = True
    # Retain the paired comparison after a failure, then stop rather than fill disk with dumps.
    if failed:
        break
sys.exit(int(failed))
