"""Retained hosted timings guard the shared-session capacity split."""
import json
import re
import unittest
from collections import defaultdict
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def partitions():
    source = (ROOT / 'scripts/run-ci-tests.ps1').read_text()
    block = source.split('$groups = [ordered]@{', 1)[1].split('\n    }', 1)[0]
    return dict(re.findall(r"^\s*'?([\w-]+)'? = '([^']+)'", block, re.M))


def matches(name, selector):
    def alternative(clause):
        clause = clause.strip('() ')
        if clause.startswith('FullyQualifiedName!~'):
            return clause[20:].lower() not in name.lower()
        if clause.startswith('FullyQualifiedName~'):
            return clause[19:].lower() in name.lower()
        raise AssertionError(clause)
    return all(any(alternative(c) for c in clause.split('|')) for clause in selector.split('&'))


class SharedCapacityTests(unittest.TestCase):
    def test_retained_intel_work_fits_with_forty_percent_headroom(self):
        fixture = json.loads((Path(__file__).parent / 'fixtures/p4-intel-shared-capacity.json').read_text())
        groups = partitions()
        for sample in fixture['samples']:
            totals = defaultdict(float)
            for row in sample['classes']:
                owners = [key for key, selector in groups.items() if matches(row['name'] + '.CapacityCase', selector)]
                self.assertEqual(len(owners), 1, (sample['artifact'], row['name'], owners))
                totals[owners[0]] += row['seconds']
            for group, seconds in totals.items():
                with self.subTest(sample=sample['artifact'], group=group):
                    self.assertLessEqual(seconds, 180, 'Measured work exhausts shared-session headroom')

    def test_new_shared_classes_still_have_exactly_one_owner(self):
        for cls in ['SharedFuture_Tests', 'SharedOneFileFuture_Tests', 'SharedOneFileWarmFuture_Tests',
                    'SharedOneFileMaintenanceFuture_Tests', 'SharedOneFileRecoveryFuture_Tests',
                    'SharedOneFileReadFuture_Tests', 'SharedReaderFuture_Tests',
                    'SharedOneFileRecoveryProcessFuture_Tests', 'SharedReaderProcessFuture_Tests']:
            name = 'LiteDB.Internals.' + cls + '.Future_case'
            self.assertEqual(sum(matches(name, selector) for selector in partitions().values()), 1, name)


if __name__ == '__main__':
    unittest.main()
