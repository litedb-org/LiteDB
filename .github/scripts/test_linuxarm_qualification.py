"""Qualification evidence must reject skips, truncation and missing classes."""
import importlib.util
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location('qualification', Path(__file__).with_name('run_linuxarm_qualification.py'))
qualification = importlib.util.module_from_spec(spec)
spec.loader.exec_module(qualification)


class QualificationEvidenceTests(unittest.TestCase):
    def check_trx(self, outcome='Passed', total=1, executed=1, session='Completed', expected=None):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'test.trx'
            path.write_text(f'''<TestRun xmlns="{qualification.NS['t']}">
<TestDefinitions><UnitTest id="1"><TestMethod className="Example.Tests" name="Guard"/></UnitTest></TestDefinitions>
<Results><UnitTestResult testId="1" testName="Guard" outcome="{outcome}"/></Results>
<ResultSummary outcome="{session}"><Counters total="{total}" executed="{executed}"/></ResultSummary>
</TestRun>''')
            return qualification.verify(path, expected if expected is not None else {'Example.Tests.Guard'})

    def test_complete_execution_is_counted(self):
        self.assertEqual(self.check_trx(), {'Example.Tests': 1})

    def test_skipped_failed_and_not_executed_cases_are_rejected(self):
        for outcome in ('NotExecuted', 'Skipped', 'Failed'):
            with self.subTest(outcome=outcome), self.assertRaises(ValueError):
                self.check_trx(outcome=outcome)

    def test_missing_discovered_case_is_rejected(self):
        with self.assertRaises(ValueError):
            self.check_trx(expected={'Example.Tests.Guard', 'Example.Tests.Missing'})

    def test_truncated_and_aborted_sessions_are_rejected(self):
        for values in ({'total': 2}, {'executed': 0}, {'session': 'Aborted'}):
            with self.subTest(values=values), self.assertRaises(ValueError):
                self.check_trx(**values)

    def test_matrix_contains_each_required_family_and_stress_contains_all_tenure(self):
        root = Path(__file__).resolve().parents[2]
        full = qualification.classes(root)
        stress = qualification.classes(root, True)
        for name in ('SharedOneFileBootstrap_Tests', 'SharedOneFileFinalClose_Tests',
                     'SharedOneFileMaintenanceCrash_Tests', 'SharedOneFileRebuildPowerLoss_Tests',
                     'SharedWriterTenureFutexProbe_Tests', 'SharedWriterTenureCoherenceBreadth_Tests',
                     'SharedWriterTenureProcess_Tests'):
            self.assertTrue(any(cls.endswith('.' + name) for cls in full), name)
        self.assertEqual(stress, [cls for cls in full if cls.rsplit('.', 1)[1].startswith('SharedWriterTenure')])


if __name__ == '__main__':
    unittest.main()
