"""Read completed VSTest reports without treating runner failures as test failures."""

from collections import Counter, defaultdict
from dataclasses import dataclass, field
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
    test_id: str = ""
    execution_id: str = ""

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
    definitions: dict = field(default_factory=dict)


def _case_key(test_id, name, occurrence):
    rendered = hashlib.sha256(name.encode("utf-8")).hexdigest()
    return f"{test_id}:{rendered}:{occurrence}"


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
        if method is None or not method.get("className") or not definition.get("name"):
            raise GateError("Missing test method definition")
        test_id = definition.get("id")
        if not test_id or test_id in definitions:
            raise GateError("Missing or duplicate test definition identity")
        definitions[test_id] = {"name": definition.get("name"),
                                "class_name": method.get("className")}
    grouped = defaultdict(list)
    executions = {}
    for result in root.findall("./Results/UnitTestResult"):
        name = result.get("testName")
        outcome = result.get("outcome")
        execution_id = result.get("executionId")
        test_id = result.get("testId")
        if not name or not execution_id or execution_id in executions:
            raise GateError(f"Missing or duplicate test execution identity: {name}")
        if outcome not in ("Passed", "Failed", "NotExecuted"):
            raise GateError(f"Incomplete test outcome {outcome}: {name}")
        definition = definitions.get(test_id)
        class_name = definition["class_name"] if definition else None
        if not class_name or not name.startswith(class_name + "."):
            raise GateError(f"Missing or inconsistent test definition: {name}")
        message = result.findtext("./Output/ErrorInfo/Message", "")
        test = TestResult(name, outcome, message, class_name, test_id, execution_id)
        if outcome == "Failed" and not test.failure:
            raise GateError(f"Failed test has no failure classification: {name}")
        executions[execution_id] = test_id
        grouped[(test_id, name)].append(test)
    if not executions:
        raise GateError("Zero tests executed")
    entries = {}
    for entry in root.findall("./TestEntries/TestEntry"):
        execution_id = entry.get("executionId")
        test_id = entry.get("testId")
        if not execution_id or execution_id in entries or test_id not in definitions:
            raise GateError("Missing or duplicate test entry identity")
        entries[execution_id] = test_id
    if entries != executions:
        raise GateError("Test entries do not exactly match completed results")
    if set(executions.values()) != set(definitions):
        raise GateError("A discovered test definition has no completed result")
    tests = {}
    for (test_id, name), cases in sorted(grouped.items()):
        cases.sort(key=lambda test: (test.outcome, test.failure, test.message))
        for occurrence, test in enumerate(cases, 1):
            tests[_case_key(test_id, name, occurrence)] = test
    for info in summary.findall(".//RunInfo"):
        if info.get("outcome") in ("Passed", "Completed"):
            continue
        text = "".join(info.itertext()).strip()
        # xUnit repeats ordinary failures/skips as RunInfo errors/warnings.
        # Admit only that exact shape naming a matching completed result.
        event = re.fullmatch(r"\[xUnit\.net [0-9:.]+\]\s+(.+) \[(FAIL|SKIP)\]", text)
        cases = [test for test in tests.values()
                 if event and test.name == event.group(1)]
        expected = {"FAIL": "Failed", "SKIP": "NotExecuted"}
        if not event or not any(case.outcome == expected[event.group(2)] for case in cases):
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
    discovery = {test_id: definition["name"] for test_id, definition in definitions.items()}
    return TestRun(tests, hashlib.sha256(raw).hexdigest(), discovery)
