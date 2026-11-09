from collections import Counter
from pathlib import Path
import argparse
import json
import subprocess
import xml.etree.ElementTree as ET

RUN = 42547542099
SHA = '7000dd6f01d0187a8fb5d030064eb327864ab731'
parser = argparse.ArgumentParser(description='Verify exact-head phase4 P1 handle-safety artifacts.')
parser.add_argument('evidence_dir', type=Path, help='Artifact cache and JSON output directory')
ROOT = parser.parse_args().evidence_dir.resolve()
run = json.loads(subprocess.check_output([
    'gh', 'run', 'view', str(RUN), '-R', 'litedb-org/LiteDB',
    '--json', 'headSha,status,conclusion,jobs']))
assert run['headSha'] == SHA, run
assert run['status'] == 'completed' and run['conclusion'] == 'success', run
assert len(run['jobs']) == 12 and all(j['conclusion'] == 'success' for j in run['jobs'])
ns = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
rows = []
for platform in ['ubuntu-latest', 'windows-latest', 'macos-latest']:
    for runtime in ['net8.0', 'net10.0']:
        for partition, expected in [('wal', 9), ('ownership', 67)]:
            leg = f'handle-safety-{platform}-{runtime}-{partition}'
            path = ROOT / str(RUN) / leg
            if not path.exists():
                subprocess.run(['gh', 'run', 'download', str(RUN), '-R', 'litedb-org/LiteDB',
                                '-n', leg, '-D', str(path)], check=True)
            files = sorted(path.rglob('*.trx'))
            assert len(files) == expected, (leg, len(files), expected)
            outcomes, counters, nonpassing = Counter(), Counter(), []
            for file in files:
                tree = ET.parse(file)
                summary = tree.find('.//t:ResultSummary', ns)
                assert summary is not None and summary.get('outcome') == 'Completed', file
                raw = summary.find('t:Counters', ns)
                assert raw is not None, file
                counters.update({k: int(v) for k, v in raw.attrib.items()})
                results = tree.findall('.//t:UnitTestResult', ns)
                assert results, ('Empty safety partition', file)
                assert len(results) == int(raw.get('total')), file
                for result in results:
                    outcome = result.get('outcome')
                    assert outcome in {'Passed', 'NotExecuted'}, (file, result.attrib)
                    outcomes[outcome] += 1
                    if outcome != 'Passed':
                        nonpassing.append(result.get('testName'))
            rows.append({'run': RUN, 'leg': leg, 'workflowCandidateSha': SHA,
                         'sessions': len(files), 'rawCounters': dict(counters),
                         'actualResultOutcomes': dict(outcomes),
                         'nonPassingNames': sorted(nonpassing)})
            print(leg, len(files), dict(outcomes))
(ROOT / 'phase4-handle-counts.json').write_text(json.dumps(rows, indent=2) + '\n')
