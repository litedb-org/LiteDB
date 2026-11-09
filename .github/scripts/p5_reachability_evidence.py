"""Render the frozen P5 gap inventory against executed (never skipped) TRX evidence."""
import argparse
import json
from pathlib import Path
import xml.etree.ElementTree as ET


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--trx', type=Path)
    parser.add_argument('--filter', action='store_true')
    parser.add_argument('--output', type=Path)
    args = parser.parse_args()
    inventory = json.loads(Path('.github/safety/p5-reachability.json').read_text())
    entries = inventory['entries']
    assert len(entries) == 58 and sum(e['hook'] for e in entries) == 17
    tests = sorted({t for e in entries for t in e['tests']})
    if args.filter:
        print('|'.join('FullyQualifiedName~' + Path(t.split('#')[0]).stem + '.' + t.split('#')[1] for t in tests))
        return
    results = []
    if args.trx:
        for path in args.trx.rglob('*.trx'):
            root = ET.parse(path).getroot()
            for result in root.iter():
                if result.tag.endswith('UnitTestResult'):
                    results.append((str(path), result.attrib['testName'], result.attrib['outcome']))
    lines = ['# P5 reachability evidence', '',
             'Baseline e507faad4: **115/173 markers; 83/100 hooks/injectors**. The 17 hook entries are also included among the 58 marker rows below.', '',
             'Passing xUnit execution is deterministic evidence, not fuzz marker telemetry. Every cited TRX must belong to a completed green hosted run on the recorded revision. New guards assert actual marker increments. Sources and workload requirements are explicit per row.', '',
             '| Marker / hook | Code path / workload | Deterministic test | CI partition / TRX (Passed) | Why fuzz misses |',
             '| --- | --- | --- | --- | --- |']
    missing = []
    for entry in entries:
        evidence = []
        for test in entry['tests']:
            source, method = test.split('#')
            needle = Path(source).stem + '.' + method
            matches = [(p, n, o) for p, n, o in results if needle in n and o == 'Passed'
                       and (entry['marker'] != 'fault-point:DarwinTruncated' or 'macos' in p)]
            evidence.extend(sorted({p for p, _, _ in matches}))
        if not evidence:
            missing.append(entry['marker'])
        lines.append('| `' + entry['marker'] + ('` (hook)' if entry['hook'] else '`') + ' | ' + entry['workload'] + '<br>' + '<br>'.join(entry['paths']) + ' | ' + '<br>'.join(entry['tests']) + ' | ' + ('<br>'.join(evidence) or 'UNPROVEN') + ' | ' + entry['whyFuzzMisses'].replace('|', '/') + ' |')
    lines += ['', 'Missing executed test evidence: ' + (', '.join(missing) or 'none')]
    if args.output:
        args.output.write_text('\n'.join(lines) + '\n')
    else:
        print('\n'.join(lines))
    if args.trx and missing:
        raise SystemExit('Unproven markers: ' + ', '.join(missing))


if __name__ == '__main__':
    main()
