"""Read completed VSTest reports without treating runner failures as test failures."""

from collections import Counter
from dataclasses import dataclass
from pathlib import Path
import hashlib
import re
import xml.etree.ElementTree as ET


class GateError(ValueError):
    """Evidence is invalid or does not satisfy the acceptance contract."""


@dataclass(frozen=True)
class TestResult:
    name: str
    outcome: str
    message: str
    class_name: str

    @property
    def failure(self):
        # Stack locations change with checkout paths and source line movement.
        # Preserve all assertion text and exception details before stack frames.
        lines = self.message.replace("\r\n", "\n").strip().splitlines()
        content = []
        for line in lines:
            if line.lstrip().startswith("at "):
                break
            content.append(line.rstrip())
        return "\n".join(content).strip()


@dataclass(frozen=True)
class TestRun:
    tests: dict
    sha256: str


def read_trx(path, exit_code):
    """Require consistent counters, completed results, and an ordinary test exit."""
    if exit_code not in (0, 1):
        raise GateError(f"Unexpected test process exit code: {exit_code}")
    raw = Path(path).read_bytes()
    try:
        root = ET.fromstring(raw)
    except ET.ParseError as error:
        raise GateError(f"Malformed TRX: {error}") from error
    # VSTest versions use different namespace URIs; retain the element names.
    for node in root.iter():
        node.tag = node.tag.rsplit("}", 1)[-1]
    summary = root.find("ResultSummary")
    if summary is None or summary.get("outcome") not in ("Completed", "Passed", "Failed"):
        raise GateError("Missing or incomplete TRX summary")
    definitions = {}
    for definition in root.findall("./TestDefinitions/UnitTest"):
        method = definition.find("TestMethod")
        if method is None or not method.get("className"):
            raise GateError("Missing test method definition")
        test_id = definition.get("id")
        if not test_id or test_id in definitions:
            raise GateError("Missing or duplicate test definition identity")
        definitions[test_id] = method.get("className")
    tests = {}
    for result in root.findall("./Results/UnitTestResult"):
        name = result.get("testName")
        outcome = result.get("outcome")
        if not name or name in tests:
            raise GateError(f"Missing or duplicate test case identity: {name}")
        if outcome not in ("Passed", "Failed", "NotExecuted"):
            raise GateError(f"Incomplete test outcome {outcome}: {name}")
        class_name = definitions.get(result.get("testId"))
        if not class_name or not name.startswith(class_name + "."):
            raise GateError(f"Missing or inconsistent test definition: {name}")
        message = result.findtext("./Output/ErrorInfo/Message", "")
        test = TestResult(name, outcome, message, class_name)
        if outcome == "Failed" and not test.failure:
            raise GateError(f"Failed test has no failure classification: {name}")
        tests[name] = test
    if not tests:
        raise GateError("Zero tests executed")
    for info in summary.findall(".//RunInfo"):
        if info.get("outcome") in ("Passed", "Completed"):
            continue
        text = "".join(info.itertext()).strip()
        # xUnit repeats ordinary failures/skips as RunInfo errors/warnings.
        # Admit only that exact shape naming a matching completed result.
        event = re.fullmatch(r"\[xUnit\.net [0-9:.]+\]\s+(.+) \[(FAIL|SKIP)\]", text)
        case = tests.get(event.group(1)) if event else None
        expected = {"FAIL": "Failed", "SKIP": "NotExecuted"}
        if case is None or case.outcome != expected[event.group(2)]:
            raise GateError("TRX contains a runner diagnostic: " + text)
    counters = summary.find("Counters")
    if counters is None:
        raise GateError("Missing TRX counters")
    counts = Counter(test.outcome for test in tests.values())
    expected = {"total": len(tests), "passed": counts["Passed"],
                "failed": counts["Failed"],
                "executed": counts["Passed"] + counts["Failed"]}
    for key, count in expected.items():
        if counters.get(key) != str(count):
            raise GateError(f"TRX counter {key} does not match results")
    for key, value in counters.attrib.items():
        if key not in expected and key not in ("completed", "notExecuted") and value != "0":
            raise GateError(f"TRX contains incomplete or abnormal counter {key}={value}")
    # VSTest 17.12 leaves notExecuted=0 for xUnit skips. Their individual
    # NotExecuted results and total/executed counters remain authoritative.
    if counters.get("notExecuted") not in ("0", str(counts["NotExecuted"])):
        raise GateError("TRX skipped counter does not match results")
    if bool(counts["Failed"]) != (exit_code == 1):
        raise GateError("Test process exit code disagrees with completed results")
    summary_failed = summary.get("outcome") == "Failed"
    if bool(counts["Failed"]) != summary_failed:
        raise GateError("TRX summary disagrees with completed test assertions")
    return TestRun(tests, hashlib.sha256(raw).hexdigest())
