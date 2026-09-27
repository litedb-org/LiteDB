#!/usr/bin/env python3
"""Report paired relative differences and a deterministic paired bootstrap interval."""
import argparse
from collections import defaultdict
import json
from pathlib import Path
import random
import statistics

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('results', type=Path)
args = parser.parse_args()
groups = defaultdict(lambda: defaultdict(dict))
for line in args.results.read_text().splitlines():
    row = json.loads(line)
    if row['returncode']:
        raise SystemExit('Input contains a failed measurement')
    result = row['result']
    groups[(result['scenario'], result.get('active', 0))][row['pair']][row['variant']] = result
print('| Workload / live slots | baseline ms | candidate ms | paired change % (95% bootstrap) | allocation bytes/op B → C |')
print('| --- | ---: | ---: | ---: | ---: |')
for (scenario, active), pairs in groups.items():
    if len(pairs) < 5 or any(set(pair) != {'baseline', 'candidate'} for pair in pairs.values()):
        raise SystemExit('Incomplete paired measurements')
    baseline = [p['baseline']['meanMs'] for p in pairs.values()]
    candidate = [p['candidate']['meanMs'] for p in pairs.values()]
    differences = [(c / b - 1) * 100 for b, c in zip(baseline, candidate)]
    rng = random.Random(3013)
    bootstrap = sorted(statistics.mean(rng.choices(differences, k=len(differences))) for _ in range(10000))
    allocated = [statistics.median(p[v]['bytesPerOperation'] for p in pairs.values()) for v in ('baseline', 'candidate')]
    print(f'| {scenario} / {active or "—"} | {statistics.median(baseline):.6f} | '
          f'{statistics.median(candidate):.6f} | {statistics.mean(differences):+.1f} '
          f'[{bootstrap[250]:+.1f}, {bootstrap[9750]:+.1f}] | {allocated[0]:.0f} → {allocated[1]:.0f} |')
