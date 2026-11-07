import tempfile
import unittest
from pathlib import Path

from p3_windows_qualification import audit, classes


class WindowsQualificationTests(unittest.TestCase):
    def audit_result(self, outcome='Passed', total=1, name='A', results=True):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'result.trx'
            result = f'<UnitTestResult testId="id" outcome="{outcome}" />' if results else ''
            path.write_text(f'''<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
<TestDefinitions><UnitTest id="id"><TestMethod className="{name}" /></UnitTest></TestDefinitions>
<Results>{result}</Results><ResultSummary><Counters total="{total}" /></ResultSummary></TestRun>''')
            return audit(path, ['A'])

    def test_pass_counts(self):
        self.assertEqual({'A': {'executed': 1, 'skipped': 0, 'failed': 0}}, self.audit_result())

    def test_skips_failures_missing_and_truncated_results_fail(self):
        for kwargs in [{'outcome': 'NotExecuted'}, {'outcome': 'Failed'}, {'total': 2},
                       {'name': 'B'}, {'results': False, 'total': 0}]:
            with self.subTest(kwargs=kwargs), self.assertRaises(RuntimeError):
                self.audit_result(**kwargs)

    def test_full_and_stress_cover_required_families(self):
        full = classes()
        stress = classes(True)
        for name in ['SharedOneFileBootstrap', 'SharedOneFileRekey', 'SharedOneFileRebuildPowerLoss',
                     'SharedOneFileFinalClose', 'SharedOneFileMaintenance', 'SharedOneFileLock']:
            self.assertIn('LiteDB.Internals.' + name + '_Tests', full)
        for name in ['SharedWriterTenure', 'SharedWriterTenureCoherenceBreadth', 'SharedWriterTenureProcess',
                     'SharedPlatformWake']:
            self.assertIn('LiteDB.Internals.' + name + '_Tests', stress)
        self.assertTrue(set(stress) <= set(full))
        self.assertEqual(len(full), sum(len(full[i::8]) for i in range(8)))


if __name__ == '__main__':
    unittest.main()
