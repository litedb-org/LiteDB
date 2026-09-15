"""Small synthetic VSTest reports for acceptance-gate tests."""

from collections import Counter
from pathlib import Path
import xml.etree.ElementTree as ET

from policy import load_issue
from trx import TestResult, TestRun


ISSUE, MANIFEST_HASH = load_issue(Path(__file__).with_name("issues.json"), 2874)
PROVENANCE = {"base_sha": "a" * 40, "test_definition_sha": "b" * 40,
              "environment": "linux-x64-net8.0", "manifest_sha256": MANIFEST_HASH,
              "failure_normalization_sha256": "d" * 64}


def target_run(baseline=True):
    tests = {}
    definitions = {}
    for case in ISSUE["regressions"] + ISSUE["controls"]:
        failure = baseline and "failure_first_line" in case
        message = case["failure_first_line"] + "\n" + "\n".join(case["failure_contains"]) if failure else ""
        name = case["name"]
        test_id = "test-" + str(len(tests))
        tests[name] = TestResult(name, "Failed" if failure else "Passed", message,
                                 "LiteDB.Tests.Issues.Issue2874_Tests", test_id)
        definitions[test_id] = name
    return TestRun(tests, "c" * 64, definitions)


def add_test(run, suffix, outcome, message=""):
    name = "LiteDB.Tests.Issues.OtherTests." + suffix
    test_id = "test-" + str(len(run.definitions))
    run.tests[name] = TestResult(name, outcome, message, "LiteDB.Tests.Issues.OtherTests",
                                 test_id)
    run.definitions[test_id] = name
    return name


def xml_report(run):
    root = ET.Element("TestRun")
    results = ET.SubElement(root, "Results")
    definitions = ET.SubElement(root, "TestDefinitions")
    entries = ET.SubElement(root, "TestEntries")
    for index, test in enumerate(run.tests.values()):
        execution_id = f"execution-{index}"
        item = ET.SubElement(results, "UnitTestResult", testName=test.name,
                             testId=test.test_id, executionId=execution_id,
                             outcome=test.outcome)
        if test.message:
            error = ET.SubElement(ET.SubElement(item, "Output"), "ErrorInfo")
            ET.SubElement(error, "Message").text = test.message
        definition = ET.SubElement(definitions, "UnitTest", id=test.test_id, name=test.name)
        ET.SubElement(definition, "TestMethod", className=test.class_name, name="Test")
        ET.SubElement(entries, "TestEntry", testId=test.test_id, executionId=execution_id)
    counts = Counter(test.outcome for test in run.tests.values())
    summary = ET.SubElement(root, "ResultSummary", outcome="Failed" if counts["Failed"] else "Completed")
    ET.SubElement(summary, "Counters", total=str(len(run.tests)), passed=str(counts["Passed"]),
                  failed=str(counts["Failed"]), notExecuted=str(counts["NotExecuted"]),
                  executed=str(counts["Passed"] + counts["Failed"]), error="0", aborted="0")
    return root
