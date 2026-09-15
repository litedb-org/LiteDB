"""Issue contracts and exact baseline-to-candidate comparisons."""

import hashlib
import json
import re
from pathlib import Path

from trx import GateError


def require_sha(value):
    if not re.fullmatch(r"[0-9a-f]{40}", value):
        raise GateError("Commit identities must be full lowercase 40-character SHAs")
    return value


def load_issue(path, issue_number):
    raw = Path(path).read_bytes()
    manifest = json.loads(raw)
    if manifest.get("schema_version") != 1:
        raise GateError("Unsupported issue manifest schema")
    issue = manifest["issues"].get(str(issue_number))
    if issue is None:
        raise GateError(f"Issue {issue_number} has no approved execution contract")
    cases = issue["regressions"] + issue["controls"]
    names = [case["name"] for case in cases]
    if not issue["regressions"] or not issue["controls"] or len(names) != len(set(names)):
        raise GateError("Contract needs unique regression and control test cases")
    return issue, hashlib.sha256(raw).hexdigest()


def verify_focused(run, issue, baseline):
    expected = {case["name"] for case in issue["regressions"] + issue["controls"]}
    if expected != set(run.tests):
        raise GateError(f"Test selection changed: missing={sorted(expected - run.tests.keys())}; "
                        f"extra={sorted(run.tests.keys() - expected)}")
    verify_target(run, issue, baseline)


def verify_target(run, issue, baseline):
    for case in issue["regressions"] + issue["controls"]:
        test = run.tests.get(case["name"])
        if test is None:
            raise GateError(f"Missing target case: {case['name']}")
        expected = "Failed" if baseline and "failure_first_line" in case else "Passed"
        if test.outcome != expected:
            raise GateError(f"Expected {expected}, found {test.outcome}: {test.name}")
        if expected == "Failed":
            if test.failure.splitlines()[0] != case["failure_first_line"]:
                raise GateError(f"Baseline failure is not the expected defect: {test.name}")
            for detail in case.get("failure_contains", []):
                if detail not in test.message:
                    raise GateError(f"Baseline lacks expected defect detail: {test.name}")


def verify_inventory(run, expected_tests):
    actual = set(run.tests)
    if actual != expected_tests:
        raise GateError(f"Test inventory mismatch: missing={sorted(expected_tests - actual)}; "
                        f"extra={sorted(actual - expected_tests)}")


def make_ledger(run, provenance, allowed_classes, expected_tests, allowed_skips):
    verify_inventory(run, expected_tests)
    tests = {}
    for name, test in sorted(run.tests.items()):
        if test.outcome == "Failed" and test.class_name not in allowed_classes:
            raise GateError(f"Unclassified baseline failure: {name}")
        if test.outcome == "NotExecuted" and name not in allowed_skips:
            raise GateError(f"Unclassified baseline skip: {name}")
        tests[name] = {"outcome": test.outcome, "failure": test.failure,
                       "class_name": test.class_name}
    return {"schema_version": 1, "provenance": provenance,
            "baseline_trx_sha256": run.sha256, "tests": tests}


def compare_ledger(ledger, run, issue, provenance, expected_tests):
    if ledger.get("schema_version") != 1 or not ledger.get("tests"):
        raise GateError("Invalid or empty baseline ledger")
    for key in ("base_sha", "test_definition_sha", "environment", "manifest_sha256"):
        if ledger.get("provenance", {}).get(key) != provenance[key]:
            raise GateError(f"Stale or mismatched baseline provenance: {key}")
    before = ledger["tests"]
    if set(before) != expected_tests:
        raise GateError("Baseline ledger does not match the independently discovered test inventory")
    verify_inventory(run, expected_tests)
    missing = set(before) - run.tests.keys()
    if missing:
        raise GateError(f"Candidate is missing baseline tests: {sorted(missing)}")
    verify_target(run, issue, baseline=False)
    target_names = {case["name"] for case in issue["regressions"]}
    errors, unexpected_passes, known_failures = [], [], []
    for name, current in sorted(run.tests.items()):
        previous = before.get(name)
        if previous is None:
            if current.outcome != "Passed":
                errors.append(f"New test does not pass: {name}")
            continue
        previous_outcome = previous.get("outcome")
        if previous_outcome not in ("Passed", "Failed", "NotExecuted"):
            raise GateError(f"Unknown baseline outcome: {name}")
        if current.class_name != previous.get("class_name"):
            errors.append(f"Test class changed: {name}")
        if previous_outcome == "Failed" and current.outcome == "Passed":
            if name not in target_names:
                unexpected_passes.append(name)
        elif previous_outcome != current.outcome:
            errors.append(f"Outcome changed {previous_outcome} -> {current.outcome}: {name}")
        elif current.outcome == "Failed":
            if current.failure != previous.get("failure"):
                errors.append(f"Known failure classification changed: {name}")
            else:
                known_failures.append(name)
    return {"accepted": not errors and not unexpected_passes, "errors": errors,
            "unexpected_passes": unexpected_passes, "known_failures": known_failures,
            "test_count": len(run.tests), "candidate_trx_sha256": run.sha256}


def load_baseline_policy(path):
    """Load reviewed identities for baseline failures and environment-dependent skips."""
    data = json.loads(Path(path).read_text(encoding="utf-8"))
    if (data.get("schema_version") != 1 or not isinstance(data.get("classes"), list)
            or not isinstance(data.get("skipped_tests"), list)):
        raise GateError("Expected schema_version=1, classes, and skipped_tests arrays")
    for key in ("classes", "skipped_tests"):
        values = data[key]
        if (not all(isinstance(name, str) and name for name in values)
                or len(values) != len(set(values))):
            raise GateError(f"{key} must contain unique nonempty names")
    return set(data["classes"]), set(data["skipped_tests"])


def load_test_inventory(path):
    data = json.loads(Path(path).read_text(encoding="utf-8"))
    tests = data.get("tests")
    if data.get("schema_version") != 1 or not isinstance(tests, list) or not tests:
        raise GateError("Expected schema_version=1 and a nonempty tests array")
    if (not all(isinstance(name, str) and name for name in tests)
            or len(tests) != len(set(tests))):
        raise GateError("Test inventory must contain unique nonempty names")
    return set(tests)
