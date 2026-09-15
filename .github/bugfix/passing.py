"""Pin and enforce the permanently passing contracts from controller-owned state."""

from collections import defaultdict
import hashlib
import json
import re

from patching import git
from state import SHA, require


def digest(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode()).hexdigest()


def passing_cases(ledger, base_sha, test_source_sha, ancestor):
    require(isinstance(ledger, dict) and ledger.get("schema_version") == 1
            and isinstance(ledger.get("issues"), dict), "Invalid accepted-test ledger")
    names = set()
    for issue, contract in ledger["issues"].items():
        require(isinstance(issue, str) and issue.isdecimal() and int(issue) > 0, "Invalid accepted issue identity")
        require(isinstance(contract, dict), "Invalid accepted issue contract")
        candidate = contract.get("candidate_sha")
        require(isinstance(candidate, str) and SHA.fullmatch(candidate), "Accepted contract lacks immutable candidate")
        require(contract.get("test_source_sha") == test_source_sha, "Accepted regression source differs from this campaign")
        require(ancestor(candidate, base_sha), f"Accepted issue {issue} is not an ancestor of the integration base")
        tests = contract.get("tests")
        require(isinstance(tests, list) and tests and all(isinstance(name, str) and name.startswith("LiteDB.") for name in tests),
                "Accepted issue has no valid permanent passing test identities")
        require(len(tests) == len(set(tests)), "Accepted issue repeats test identities")
        names.update(tests)
    return sorted(names)


def load_snapshot(repository, repo, data_sha, base_sha, test_source_sha):
    require(isinstance(data_sha, str) and SHA.fullmatch(data_sha), "An immutable controller data commit is required")
    git(repository, "fetch", "--quiet", f"https://github.com/{repo}.git", data_sha)
    require(git(repository, "rev-parse", "--verify", data_sha + "^{commit}") == data_sha, "Controller data commit is missing")
    entry = git(repository, "ls-tree", data_sha, "--", "accepted-tests.json")
    if entry:
        require(entry.split()[0] == "100644", "Accepted ledger is not an ordinary data file")
        raw = git(repository, "show", f"{data_sha}:accepted-tests.json")
        require(len(raw) <= 8 * 1024 * 1024, "Accepted ledger exceeds the supported bound")
        ledger = json.loads(raw)
    else:
        ledger = {"schema_version": 1, "issues": {}}
    tests = passing_cases(ledger, base_sha, test_source_sha,
                          lambda candidate, base: git(repository, "merge-base", candidate, base) == candidate)
    snapshot = {"schema_version": 1, "state_commit": data_sha, "ledger_sha256": digest(ledger),
                "cases_sha256": digest(tests), "case_count": len(tests)}
    return snapshot, tests


def selection_filter(tests):
    methods = sorted({name.split("(", 1)[0] for name in tests})
    require(all(re.fullmatch(r"LiteDB(?:\.[A-Za-z_][A-Za-z0-9_]*)+", method) for method in methods),
            "Accepted test identity cannot be expressed as an exact method filter")
    return "|".join("FullyQualifiedName=" + method for method in methods)


def assert_passing(run, tests, variant):
    by_name = defaultdict(list)
    for result in run.tests.values():
        by_name[result.name].append(result)
    missing = sorted(set(tests) - by_name.keys())
    failed = sorted(name for name in tests if any(result.outcome != "Passed" for result in by_name[name]))
    require(not missing and not failed,
            f"Previously accepted tests regressed on {variant}: missing={missing}; not_passed={failed}")
