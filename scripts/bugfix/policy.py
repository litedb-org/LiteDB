"""Issue contracts and exact baseline-to-candidate comparisons."""

import hashlib
import json
import re
from collections import Counter
from pathlib import Path

from failure_normalization import canonical_failure
from focused_baseline import expectation, validate as validate_focused_baseline
from trx import GateError
from source_context import bind_observations, consume


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
    validate_focused_baseline(issue)
    return issue, hashlib.sha256(raw).hexdigest()


def verify_focused(run, issue, baseline, environment=None,
                   failure_normalization=None, normalization_sha=None):
    expected = Counter(case["name"] for case in issue["regressions"] + issue["controls"])
    actual = Counter(test.name for test in run.tests.values())
    if expected != actual:
        raise GateError(f"Test selection changed: missing={sorted((expected - actual).elements())}; "
                        f"extra={sorted((actual - expected).elements())}")
    verify_target(run, issue, baseline, environment, failure_normalization,
                  normalization_sha)


def verify_target(run, issue, baseline, environment=None,
                  failure_normalization=None, normalization_sha=None):
    focused = validate_focused_baseline(issue)
    if focused is not None:
        if (environment not in issue["environments"]
                or not isinstance(failure_normalization, dict)
                or normalization_sha != focused["failure_normalization_sha256"]):
            raise GateError("Focused baseline policy or environment does not match the contract")
    for case in issue["regressions"] + issue["controls"]:
        matches = [test for test in run.tests.values() if test.name == case["name"]]
        if len(matches) != 1:
            raise GateError(f"Missing target case: {case['name']}")
        test = matches[0]
        if focused is not None and test.test_id != case["test_id"]:
            raise GateError(f"Focused test identity changed: {test.name}")
        contract_expectation = expectation(issue, case, environment) if focused else None
        expected = (contract_expectation["outcome"] if baseline and focused is not None
                    else "Failed" if baseline and "failure_first_line" in case
                    else "Passed")
        if test.outcome != expected:
            raise GateError(f"Expected {expected}, found {test.outcome}: {test.name}")
        if expected == "Failed":
            if focused is not None:
                classified = canonical_failure(test.name, test.failure,
                                               failure_normalization, baseline=True)
                classification = case["failure_classifications"][
                    contract_expectation["classification"]]
                first_line = classified.splitlines()[0] if classified else ""
                if (first_line != classification["failure_first_line"]
                        or hashlib.sha256(classified.encode("utf-8")).hexdigest()
                        != classification["sha256"]):
                    raise GateError(f"Baseline failure is not the expected defect: {test.name}")
            else:
                if test.failure.splitlines()[0] != case["failure_first_line"]:
                    raise GateError(f"Baseline failure is not the expected defect: {test.name}")
                for detail in case.get("failure_contains", []):
                    if detail not in test.message:
                        raise GateError(f"Baseline lacks expected defect detail: {test.name}")


def verify_focused_pair(baseline_run, candidate_run, issue):
    """Bind a focused candidate to exactly the discovered baseline test identities."""
    if validate_focused_baseline(issue) is None:
        return
    if baseline_run.definitions != candidate_run.definitions:
        raise GateError("Focused candidate test definition identities changed")
    if set(baseline_run.tests) != set(candidate_run.tests):
        raise GateError("Focused candidate result instances changed")


def verify_inventory(run, expected_tests):
    actual = Counter(run.definitions.values())
    if actual != expected_tests:
        raise GateError(f"Test inventory mismatch: missing={sorted((expected_tests - actual).elements())}; "
                        f"extra={sorted((actual - expected_tests).elements())}")
    executed = {test.test_id for test in run.tests.values()}
    if executed != set(run.definitions):
        raise GateError("Discovered test definitions do not all have completed results")


def make_ledger(run, provenance, allowed_classes, expected_tests, allowed_skips,
                failure_normalization, intermittent_classes=None):
    verify_inventory(run, expected_tests)
    tests = {}
    for key, test in sorted(run.tests.items()):
        if test.outcome == "Failed" and test.class_name not in allowed_classes:
            raise GateError(f"Unclassified baseline failure: {test.name}")
        if test.outcome == "NotExecuted" and test.name not in allowed_skips:
            raise GateError(f"Unclassified baseline skip: {test.name}")
        failure = canonical_failure(test.name, test.failure, failure_normalization,
                                    baseline=test.outcome == "Failed")
        tests[key] = {"name": test.name, "test_id": test.test_id,
                      "outcome": test.outcome, "failure": failure,
                      "class_name": test.class_name}
    return {"schema_version": 1, "provenance": provenance,
            "baseline_trx_sha256": run.sha256, "definitions": run.definitions,
            "intermittent_classes": sorted(intermittent_classes or set()),
            "tests": tests}


def compare_ledger(ledger, run, issue, provenance, expected_tests,
                   failure_normalization, source_observations=()):
    if (ledger.get("schema_version") != 1 or not ledger.get("tests")
            or not isinstance(ledger.get("definitions"), dict)):
        raise GateError("Invalid or empty baseline ledger")
    for key in ("base_sha", "test_definition_sha", "environment", "manifest_sha256",
                "failure_normalization_sha256"):
        if ledger.get("provenance", {}).get(key) != provenance[key]:
            raise GateError(f"Stale or mismatched baseline provenance: {key}")
    before = ledger["tests"]
    intermittent_classes = ledger.get("intermittent_classes")
    if (not isinstance(intermittent_classes, list)
            or not all(isinstance(name, str) and name for name in intermittent_classes)
            or len(intermittent_classes) != len(set(intermittent_classes))):
        raise GateError("Invalid intermittent class policy in baseline ledger")
    intermittent_classes = set(intermittent_classes)
    baseline_definitions = ledger["definitions"]
    if Counter(baseline_definitions.values()) != expected_tests:
        raise GateError("Baseline ledger does not match the independently discovered test inventory")
    verify_inventory(run, expected_tests)
    if baseline_definitions != run.definitions:
        raise GateError("Candidate test definition identities changed")
    missing = set(before) - run.tests.keys()
    extra = run.tests.keys() - set(before)
    if missing or extra:
        raise GateError(f"Candidate result instances changed: missing={sorted(missing)}; "
                        f"extra={sorted(extra)}")
    verify_target(run, issue, baseline=False, environment=provenance["environment"],
                  failure_normalization=failure_normalization,
                  normalization_sha=provenance["failure_normalization_sha256"])
    bind_observations(issue, provenance, source_observations)
    target_names = {case["name"] for case in issue["regressions"]}
    errors, unexpected_passes, known_failures = [], [], []
    inconclusive_changes, classification_changes = [], []
    source_context_changes = []
    for key, current in sorted(run.tests.items()):
        previous = before.get(key)
        name = current.name
        if previous is None:
            raise GateError(f"Candidate result instance lacks a baseline identity: {name}")
        previous_outcome = previous.get("outcome")
        if previous_outcome not in ("Passed", "Failed", "NotExecuted"):
            raise GateError(f"Unknown baseline outcome: {name}")
        if current.class_name != previous.get("class_name"):
            errors.append(f"Test class changed: {name}")
        if (previous_outcome != current.outcome
                and current.class_name in intermittent_classes
                and {previous_outcome, current.outcome} == {"Passed", "Failed"}):
            inconclusive_changes.append({"name": name, "class_name": current.class_name,
                                         "baseline_outcome": previous_outcome,
                                         "candidate_outcome": current.outcome})
        elif previous_outcome == "Failed" and current.outcome == "Passed":
            if current.name not in target_names:
                observed = consume(previous, vars(current), source_observations)
                if observed is None:
                    unexpected_passes.append(name)
                else:
                    source_context_changes.append(observed)
        elif previous_outcome != current.outcome:
            errors.append(f"Outcome changed {previous_outcome} -> {current.outcome}: {name}")
        elif current.outcome == "Failed":
            failure = canonical_failure(current.name, current.failure,
                                        failure_normalization)
            baseline_failure = previous.get("failure")
            if not isinstance(baseline_failure, str):
                raise GateError(f"Invalid baseline failure classification: {name}")
            if failure != baseline_failure:
                errors.append(f"Known failure classification changed: {name}")
                classification_changes.append({
                    "name": name,
                    "class_name": current.class_name,
                    "baseline_failure_sha256": hashlib.sha256(
                        baseline_failure.encode("utf-8")).hexdigest(),
                    "candidate_failure_sha256": hashlib.sha256(
                        failure.encode("utf-8")).hexdigest(),
                })
            else:
                known_failures.append(name)
    if source_context_changes != list(source_observations):
        errors.append("Declared source-context change did not match the completed frozen guard transition")
    return {"accepted": not errors and not unexpected_passes and not inconclusive_changes,
            "errors": errors, "inconclusive_changes": inconclusive_changes,
            "classification_changes": classification_changes,
            "unexpected_passes": unexpected_passes, "known_failures": known_failures,
            "source_context_changes": source_context_changes,
            "test_count": len(run.tests), "candidate_trx_sha256": run.sha256}


def load_baseline_policy(path):
    """Load reviewed identities for baseline failures and environment-dependent skips."""
    data = json.loads(Path(path).read_text(encoding="utf-8"))
    if (data.get("schema_version") != 1 or not isinstance(data.get("classes"), list)
            or not isinstance(data.get("skipped_tests"), list)
            or not isinstance(data.get("intermittent_classes"), list)):
        raise GateError(
            "Expected schema_version=1, classes, skipped_tests, and intermittent_classes arrays")
    for key in ("classes", "skipped_tests", "intermittent_classes"):
        values = data[key]
        if (not all(isinstance(name, str) and name for name in values)
                or len(values) != len(set(values))):
            raise GateError(f"{key} must contain unique nonempty names")
    intermittent = set(data["intermittent_classes"])
    if not intermittent <= set(data["classes"]):
        raise GateError("intermittent_classes must be classified baseline failure classes")
    return set(data["classes"]), set(data["skipped_tests"]), intermittent


def load_test_inventory(path):
    data = json.loads(Path(path).read_text(encoding="utf-8"))
    tests = data.get("tests")
    if data.get("schema_version") != 1 or not isinstance(tests, list) or not tests:
        raise GateError("Expected schema_version=1 and a nonempty tests array")
    if not all(isinstance(name, str) and name for name in tests):
        raise GateError("Test inventory must contain nonempty names")
    return Counter(tests)
