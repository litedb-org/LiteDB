import copy
from pathlib import Path
import tempfile
import unittest
import xml.etree.ElementTree as ET

from test_support import add_test, target_run, xml_report
from trx import GateError, TestRun, read_trx


class TrxTests(unittest.TestCase):
    def parse(self, root, exit_code=1):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "test.trx"
            ET.ElementTree(root).write(path, encoding="utf-8", xml_declaration=True)
            return read_trx(path, exit_code)

    def test_valid_failed_and_successful_reports(self):
        self.assertEqual(7, len(self.parse(xml_report(target_run())).tests))
        self.assertEqual(7, len(self.parse(xml_report(target_run(False)), 0).tests))

    def test_zero_selection_rejected(self):
        with self.assertRaisesRegex(GateError, "Zero tests"):
            self.parse(xml_report(TestRun({}, "")), 0)

    def test_exit_code_must_match_assertions(self):
        for run, code in ((target_run(), 0), (target_run(False), 1), (target_run(), 137)):
            with self.subTest(code=code), self.assertRaises(GateError):
                self.parse(xml_report(run), code)

    def test_summary_must_match_assertions(self):
        root = xml_report(target_run())
        root.find("ResultSummary").set("outcome", "Passed")
        with self.assertRaisesRegex(GateError, "summary disagrees"):
            self.parse(root)

        root = xml_report(target_run(False))
        root.find("ResultSummary").set("outcome", "Failed")
        with self.assertRaisesRegex(GateError, "summary disagrees"):
            self.parse(root, 0)

    def test_duplicate_display_identity_rejected(self):
        root = xml_report(target_run())
        root.find("Results").append(copy.deepcopy(root.find("./Results/UnitTestResult")))
        with self.assertRaisesRegex(GateError, "duplicate"):
            self.parse(root)

    def test_result_without_definition_rejected(self):
        root = xml_report(target_run())
        root.find("TestDefinitions").clear()
        with self.assertRaisesRegex(GateError, "definition"):
            self.parse(root)

    def test_counter_mismatch_rejected(self):
        root = xml_report(target_run())
        root.find("./ResultSummary/Counters").set("total", "8")
        with self.assertRaisesRegex(GateError, "counter total"):
            self.parse(root)

    def test_aborted_run_rejected(self):
        root = xml_report(target_run())
        root.find("ResultSummary").set("outcome", "Aborted")
        with self.assertRaisesRegex(GateError, "incomplete"):
            self.parse(root)

    def test_runner_error_after_completed_tests_rejected(self):
        root = xml_report(target_run())
        info = ET.SubElement(ET.SubElement(root.find("ResultSummary"), "RunInfos"),
                             "RunInfo", outcome="Error")
        ET.SubElement(info, "Text").text = "Test host process crashed"
        with self.assertRaisesRegex(GateError, "runner diagnostic"):
            self.parse(root)

    def test_xunit_repeated_failure_log_matches_actual_failed_case(self):
        root = xml_report(target_run())
        info = ET.SubElement(ET.SubElement(root.find("ResultSummary"), "RunInfos"),
                             "RunInfo", outcome="Error")
        text = ET.SubElement(info, "Text")
        text.text = "[xUnit.net 00:00:00.40]     " + next(iter(target_run().tests)) + " [FAIL]"
        self.assertEqual(7, len(self.parse(root).tests))
        text.text = "[xUnit.net 00:00:00.40]     Unknown.Test [FAIL]"
        with self.assertRaisesRegex(GateError, "runner diagnostic"):
            self.parse(root)

    def test_unknown_test_outcome_rejected(self):
        root = xml_report(target_run())
        root.find("./Results/UnitTestResult").set("outcome", "Timeout")
        with self.assertRaisesRegex(GateError, "Incomplete test"):
            self.parse(root)

    def test_xunit_skip_warning_and_zero_legacy_counter_supported(self):
        run = target_run(False)
        name = add_test(run, "Skipped", "NotExecuted")
        root = xml_report(run)
        root.find("./ResultSummary/Counters").set("notExecuted", "0")
        info = ET.SubElement(ET.SubElement(root.find("ResultSummary"), "RunInfos"),
                             "RunInfo", outcome="Warning")
        ET.SubElement(info, "Text").text = "[xUnit.net 00:00:00.23]     " + name + " [SKIP]"
        self.assertEqual("NotExecuted", self.parse(root, 0).tests[name].outcome)


if __name__ == "__main__":
    unittest.main()
