"""Validate exact environment-aware focused baseline contracts."""

import hashlib
import json
import re


SHA = re.compile(r"[0-9a-f]{40}\Z")
SHA256 = re.compile(r"[0-9a-f]{64}\Z")
TEST_ID = re.compile(r"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\Z")
ENVIRONMENT = re.compile(r"(?:linux|windows|macos)-(?:x64|x86|arm64)-net(?:8|10)\.0\Z")


class FocusedBaselineError(ValueError):
    """The reviewed focused baseline contract is malformed or incomplete."""


def require(condition, message):
    if not condition:
        raise FocusedBaselineError(message)


def digest(value):
    raw = json.dumps(value, sort_keys=True, separators=(",", ":"),
                     ensure_ascii=False).encode("utf-8")
    return hashlib.sha256(raw).hexdigest()


def validate(issue):
    """Return the optional focused baseline after strict whole-contract validation."""
    baseline = issue.get("focused_baseline")
    if baseline is None:
        return None
    expected_fields = {
        "schema_version", "source_run_id", "source_sha", "repeat_candidate_sha",
        "workflow_sha", "failure_normalization_sha256", "artifacts", "control_gaps",
    }
    require(isinstance(baseline, dict) and set(baseline) == expected_fields
            and baseline.get("schema_version") == 1,
            "Invalid environment-aware focused baseline header")
    environments = issue.get("environments")
    require(isinstance(environments, list) and environments
            and len(environments) == len(set(environments))
            and all(isinstance(value, str) and ENVIRONMENT.fullmatch(value)
                    for value in environments),
            "Focused baseline requires unique exact environments")
    require(baseline["source_sha"] == issue.get("frozen_test_revision")
            and SHA.fullmatch(baseline["source_sha"] or ""),
            "Focused baseline source differs from frozen tests")
    require(type(baseline["source_run_id"]) is int and baseline["source_run_id"] > 0
            and SHA.fullmatch(baseline["repeat_candidate_sha"] or "")
            and SHA.fullmatch(baseline["workflow_sha"] or "")
            and SHA256.fullmatch(baseline["failure_normalization_sha256"] or ""),
            "Focused baseline provenance is incomplete")
    artifacts = baseline["artifacts"]
    require(isinstance(artifacts, dict) and set(artifacts) == set(environments),
            "Focused baseline artifact environments are incomplete")
    artifact_ids = set()
    for environment, artifact in artifacts.items():
        require(isinstance(artifact, dict) and set(artifact) == {
            "artifact_id", "artifact_name", "baseline_trx_sha256",
            "repeat_candidate_trx_sha256"},
            f"Invalid focused baseline artifact: {environment}")
        require(type(artifact["artifact_id"]) is int and artifact["artifact_id"] > 0
                and artifact["artifact_id"] not in artifact_ids
                and isinstance(artifact["artifact_name"], str)
                and artifact["artifact_name"]
                and SHA256.fullmatch(artifact["baseline_trx_sha256"] or "")
                and SHA256.fullmatch(artifact["repeat_candidate_trx_sha256"] or ""),
                f"Incomplete focused baseline artifact identity: {environment}")
        artifact_ids.add(artifact["artifact_id"])
    cases = issue.get("regressions", []) + issue.get("controls", [])
    components = set()
    controlled = set()
    for role, rows in (("Failed", issue.get("regressions")),
                       ("Passed", issue.get("controls"))):
        require(isinstance(rows, list) and rows,
                "Focused baseline requires regression and control cases")
        for case in rows:
            required = {"name", "test_id", "component", "baseline_by_environment"}
            allowed = required | ({"failure_classifications"} if role == "Failed" else set())
            require(isinstance(case, dict) and set(case) == allowed
                    and isinstance(case["name"], str) and case["name"].startswith("LiteDB.")
                    and TEST_ID.fullmatch(case["test_id"] or "")
                    and isinstance(case["component"], str) and case["component"],
                    f"Invalid focused baseline case: {case.get('name') if isinstance(case, dict) else case}")
            components.add(case["component"])
            if role == "Passed":
                controlled.add(case["component"])
            by_environment = case["baseline_by_environment"]
            require(isinstance(by_environment, dict)
                    and set(by_environment) == set(environments),
                    f"Focused baseline environments changed: {case['name']}")
            classifications = case.get("failure_classifications", {})
            require(isinstance(classifications, dict)
                    and bool(classifications) == (role == "Failed"),
                    f"Focused baseline classifications changed: {case['name']}")
            for label, classification in classifications.items():
                require(isinstance(label, str) and label
                        and isinstance(classification, dict)
                        and set(classification) == {"failure_first_line", "sha256"}
                        and isinstance(classification["failure_first_line"], str)
                        and classification["failure_first_line"]
                        and "\n" not in classification["failure_first_line"]
                        and SHA256.fullmatch(classification["sha256"] or ""),
                        f"Invalid focused failure classification: {case['name']}")
            used = set()
            for environment, expectation in by_environment.items():
                expected = {"outcome", "classification"} if role == "Failed" else {"outcome"}
                require(isinstance(expectation, dict) and set(expectation) == expected
                        and expectation["outcome"] == role,
                        f"Wrong focused baseline role in {environment}: {case['name']}")
                if role == "Failed":
                    require(expectation["classification"] in classifications,
                            f"Unknown focused failure classification: {case['name']}")
                    used.add(expectation["classification"])
            require(used == set(classifications),
                    f"Unused focused failure classification: {case['name']}")
    gaps = baseline["control_gaps"]
    require(isinstance(gaps, list), "Focused baseline control gaps must be a list")
    gap_components = set()
    for gap in gaps:
        require(isinstance(gap, dict) and set(gap) == {"component", "status", "reason"}
                and gap["status"] == "control_gap"
                and isinstance(gap["component"], str) and gap["component"] in components
                and gap["component"] not in controlled
                and gap["component"] not in gap_components
                and isinstance(gap["reason"], str) and gap["reason"].strip(),
                "Invalid focused baseline control gap")
        gap_components.add(gap["component"])
    require(components - controlled == gap_components,
            "Every uncontrolled focused component needs an explicit control gap")
    return baseline


def expectation(issue, case, environment):
    baseline = validate(issue)
    require(baseline is not None, "Issue has no environment-aware focused baseline")
    require(environment in case["baseline_by_environment"],
            f"No focused baseline for environment: {environment}")
    return case["baseline_by_environment"][environment]


def contract_digest(issue):
    baseline = validate(issue)
    return digest({
        "focused_baseline": baseline,
        "regressions": issue["regressions"],
        "controls": issue["controls"],
    }) if baseline is not None else None
