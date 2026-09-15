"""Exact-case normalization for reviewed volatile failure diagnostics."""

import hashlib
import json
from pathlib import Path
import re


class FailureNormalizationError(ValueError):
    """The reviewed normalization policy or baseline failure is invalid."""


def load_failure_normalization(path):
    raw = Path(path).read_bytes()
    data = json.loads(raw)
    tests = data.get("tests")
    if data.get("schema_version") != 1 or not isinstance(tests, dict):
        raise FailureNormalizationError(
            "Expected failure normalization schema_version=1 and tests object")
    for name, rules in tests.items():
        if not isinstance(name, str) or not name or not isinstance(rules, list) or not rules:
            raise FailureNormalizationError(
                "Failure normalization requires exact test names and nonempty rules")
        for rule in rules:
            matches = rule.get("matches") if isinstance(rule, dict) else None
            valid_matches = (type(matches) is int and matches >= 1)
            if isinstance(matches, list):
                valid_matches = (bool(matches)
                                 and all(type(value) is int and value >= 1
                                         for value in matches)
                                 and len(matches) == len(set(matches)))
            if (not isinstance(rule, dict)
                    or set(rule) != {"pattern", "replacement", "matches"}
                    or not isinstance(rule["pattern"], str) or not rule["pattern"]
                    or not isinstance(rule["replacement"], str)
                    or not valid_matches):
                raise FailureNormalizationError(f"Invalid failure normalization rule: {name}")
            try:
                expression = re.compile(rule["pattern"])
                expression.sub(rule["replacement"], "")
            except re.error as error:
                raise FailureNormalizationError(
                    f"Invalid failure normalization regex: {name}") from error
            if expression.match(""):
                raise FailureNormalizationError(
                    f"Failure normalization cannot match empty text: {name}")
    return tests, hashlib.sha256(raw).hexdigest()


def canonical_failure(name, failure, policy, baseline=False):
    rules = policy.get(name)
    if rules is None:
        return failure
    original = failure
    normalized = failure
    for rule in rules:
        normalized, count = re.subn(rule["pattern"], rule["replacement"], normalized)
        expected_counts = (set(rule["matches"])
                           if isinstance(rule["matches"], list)
                           else {rule["matches"]})
        if count not in expected_counts:
            if baseline:
                raise FailureNormalizationError(
                    f"Baseline failure does not match reviewed normalization: {name}")
            return original
    return normalized
