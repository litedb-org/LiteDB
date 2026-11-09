import tempfile
import unittest
from pathlib import Path

from p3_windows_qualification import audit, classes, linux_only_methods, qualification_filter


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
                     'SharedOneFileFinalClose', 'SharedOneFileMaintenance', 'SharedOneFileLock',
                     'SharedOneFileTransactionOwnership']:
            self.assertIn('LiteDB.Internals.' + name + '_Tests', full)
        for name in ['SharedWriterTenure', 'SharedWriterTenureCoherenceBreadth', 'SharedWriterTenureProcess',
                     'SharedPlatformWake']:
            self.assertIn('LiteDB.Internals.' + name + '_Tests', stress)
        self.assertTrue(set(stress) <= set(full))
        self.assertEqual(len(full), sum(len(full[i::8]) for i in range(8)))

    def test_mixed_wake_class_retains_portable_theory_and_inventories_linux_syscalls(self):
        name = 'LiteDB.Internals.SharedWriterTenureWakeSafety_Tests'
        expected = {
            'Final_facade_disposal_wakes_a_holder_proven_inside_the_shared_futex_syscall',
            'Wait_bounds_follow_pressure_and_EINTR_and_spurious_wakes_recheck_without_losing_the_holder',
            'Unexpected_wait_error_drains_a_live_operation_before_releasing_storage',
            'Low_32_bit_wake_sequence_wrap_still_refuses_the_pre_wrap_wait_word',
        }
        self.assertEqual(expected, set(linux_only_methods(name)))
        selected = qualification_filter(name)
        self.assertTrue(selected.startswith(f'FullyQualifiedName~{name}.'))
        self.assertEqual(4, selected.count('&FullyQualifiedName!~'))
        self.assertNotIn('Wake_at_every_sample_recheck_and_syscall_edge', selected)
        self.assertIn(name, classes())
        self.assertIn(name, classes(True))

    def test_portable_request_safety_has_no_platform_exclusions(self):
        name = 'LiteDB.Internals.SharedWriterTenureRequestSafety_Tests'
        self.assertEqual([], linux_only_methods(name))
        self.assertEqual(f'FullyQualifiedName~{name}.', qualification_filter(name))


if __name__ == '__main__':
    unittest.main()
