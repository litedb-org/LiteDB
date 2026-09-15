#!/usr/bin/env python3
"""Compare trusted, case-level evidence from paired full CI runs.

The evidence producer is responsible for authenticating GitHub artifacts and
normalizing TRX failures and ReproRunner reports.  This gate deliberately does
no live GitHub lookup, so a candidate checkout cannot influence what it reads.
"""

import argparse
import hashlib
import json
from pathlib import Path
import re
import sys

from bugfix_full_ci_quarantine import load_quarantine


SHA = re.compile(r"[0-9a-f]{40}")
TEST_OUTCOMES = {"passed", "failed", "skipped"}
REPRO_VERDICTS = {"bug_present", "behavior_correct", "harness_error", "inconclusive"}


class EvidenceError(ValueError):
    """The supplied evidence is malformed or cannot support acceptance."""


def require(condition, message):
    if not condition:
        raise EvidenceError(message)


def require_sha(value, field):
    require(isinstance(value, str) and SHA.fullmatch(value),
            f"{field} must be a full lowercase commit SHA")
    return value


def load_json(path):
    value = json.loads(Path(path).read_text(encoding="utf-8"))
    require(isinstance(value, dict), f"Expected a JSON object in {path}")
    return value


def load_contract(path, issue_number):
    manifest = load_json(path)
    require(manifest.get("schema_version") == 1, "Unsupported issue manifest schema")
    issue = manifest.get("issues", {}).get(str(issue_number))
    require(isinstance(issue, dict), f"Issue {issue_number} has no execution contract")
    regressions = issue.get("regressions")
    controls = issue.get("controls")
    require(isinstance(regressions, list) and regressions, "Contract has no regressions")
    require(isinstance(controls, list) and controls, "Contract has no controls")
    cases = regressions + controls
    names = [case.get("name") for case in cases]
    require(all(isinstance(name, str) and name for name in names),
            "Contract contains an unnamed test case")
    require(len(names) == len(set(names)), "Contract test identities are not unique")
    return regressions, controls


def load_intermittent_classes(path):
    raw = Path(path).read_bytes()
    policy = json.loads(raw)
    classes = policy.get("classes")
    intermittent = policy.get("intermittent_classes")
    require(policy.get("schema_version") == 1 and isinstance(classes, list)
            and isinstance(intermittent, list), "Invalid baseline failure-class policy")
    require(all(isinstance(name, str) and name for name in classes + intermittent),
            "Baseline failure classes must be nonempty strings")
    require(len(classes) == len(set(classes))
            and len(intermittent) == len(set(intermittent)),
            "Baseline failure classes must be unique")
    require(set(intermittent) <= set(classes),
            "Intermittent classes must be classified baseline failures")
    return set(intermittent), hashlib.sha256(raw).hexdigest()


def validate_test(test, job_name):
    require(isinstance(test, dict), f"Malformed test evidence in {job_name}")
    identity = test.get("identity")
    name = test.get("name")
    outcome = test.get("outcome")
    class_name = test.get("class_name")
    require(isinstance(identity, str) and identity, f"Test has no stable identity in {job_name}")
    require(isinstance(name, str) and name, f"Unnamed test evidence in {job_name}")
    require(outcome in TEST_OUTCOMES, f"Incomplete outcome for {name} in {job_name}")
    require(isinstance(class_name, str) and name.startswith(class_name + "."),
            f"Missing or inconsistent test class for {name} in {job_name}")
    if outcome == "failed":
        require(isinstance(test.get("failure_classification"), str)
                and test["failure_classification"].strip(),
                f"Failed test has no classification: {name} in {job_name}")
        require(isinstance(test.get("failure"), str) and test["failure"].strip(),
                f"Failed test has no failure detail: {name} in {job_name}")
    return identity


def validate_job(job):
    require(isinstance(job, dict), "Malformed job evidence")
    name = job.get("name")
    kind = job.get("kind")
    conclusion = job.get("conclusion")
    require(isinstance(name, str) and name, "Job evidence has no name")
    require(job.get("status") == "completed", f"Job did not complete: {name}")
    require(kind in ("tests", "repro", "check"), f"Unknown job kind in {name}")
    require(conclusion in ("success", "failure"), f"Abnormal job conclusion in {name}")
    if kind == "tests":
        tests = job.get("tests")
        require(isinstance(tests, list) and tests, f"Test job has zero results: {name}")
        names = [validate_test(test, name) for test in tests]
        require(len(names) == len(set(names)), f"Duplicate test identity in {name}")
        expected = "failure" if any(test["outcome"] == "failed" for test in tests) else "success"
        require(conclusion == expected, f"Job conclusion disagrees with test results: {name}")
    elif kind == "repro":
        verdict = job.get("verdict")
        require(verdict in REPRO_VERDICTS, f"Repro has no explicit verdict: {name}")
        require(isinstance(job.get("classification"), str) and job["classification"].strip(),
                f"Repro has no outcome classification: {name}")
        if verdict in ("bug_present", "behavior_correct"):
            require(conclusion == "success", f"Repro verdict disagrees with its job: {name}")
    else:
        require(conclusion == "success", f"Required CI check failed: {name}")
    return name


def load_evidence(path, label, issue_number, expected_sha, quarantine, workflow_path,
                  failure_normalization_sha):
    evidence = load_json(path)
    require(evidence.get("schema_version") == 1, f"Unsupported {label} evidence schema")
    require(evidence.get("accepted") is True, f"{label} collection was not accepted")
    require(evidence.get("issue") == issue_number, f"{label} evidence is for another issue")
    require(evidence.get("source_sha") == expected_sha, f"{label} run tested the wrong commit")
    require(evidence.get("role") == label, f"{label} evidence has the wrong collection role")
    require(evidence.get("failure_normalization_sha256") == failure_normalization_sha,
            f"{label} used a different failure-normalization policy")
    require(evidence.get("quarantine_sha256") == quarantine["sha256"],
            f"{label} used a different quarantine policy")
    require(evidence.get("coverage_gaps") == quarantine["coverage_gaps"],
            f"{label} coverage gaps do not match trusted policy")
    require_sha(evidence.get("evidence_definition_sha"),
                f"{label}.evidence_definition_sha")
    run = evidence.get("run")
    require(isinstance(run, dict), f"{label} evidence has no run provenance")
    require(isinstance(run.get("id"), int) and run["id"] > 0, f"{label} run ID is invalid")
    require(run.get("head_sha") == evidence["evidence_definition_sha"],
            f"{label} run used the wrong evidence definition")
    require(run.get("workflow_path") == workflow_path, f"{label} used the wrong workflow")
    require(run.get("status") == "completed", f"{label} run is incomplete")
    require(run.get("event") in ("pull_request", "workflow_dispatch"),
            f"{label} run has an unexpected event")
    require(run.get("conclusion") in ("success", "failure"),
            f"{label} run has an abnormal conclusion")
    jobs = evidence.get("jobs")
    require(isinstance(jobs, list) and len(jobs) == quarantine["remaining_jobs"],
            f"{label} must contain exactly {quarantine['remaining_jobs']} jobs")
    names = [validate_job(job) for job in jobs]
    require(len(names) == len(set(names)), f"{label} contains duplicate job names")
    require(not (quarantine["jobs"] & set(names)),
            f"{label} executed an explicitly quarantined job")
    expected_conclusion = "failure" if any(job["conclusion"] == "failure" for job in jobs) else "success"
    require(run["conclusion"] == expected_conclusion,
            f"{label} run conclusion disagrees with its jobs")
    return evidence, {job["name"]: job for job in jobs}


def tests_by_identity(job):
    return {test["identity"]: test for test in job["tests"]}


def target_tests(job, target_names):
    matches = {}
    for test in job["tests"]:
        if test["name"] in target_names:
            require(test["name"] not in matches,
                    f"Duplicate target display identity in {job['name']}: {test['name']}")
            matches[test["name"]] = test
    return matches


def verify_baseline_target(job, regressions, controls, errors):
    tests = target_tests(job, {case["name"] for case in regressions + controls})
    for case in regressions:
        test = tests[case["name"]]
        if test["outcome"] != "failed":
            errors.append(f"Baseline target is not failed in {job['name']}: {case['name']}")
            continue
        lines = test["failure"].replace("\r\n", "\n").splitlines()
        if lines[0] != case.get("failure_first_line"):
            errors.append(f"Baseline target has the wrong defect in {job['name']}: {case['name']}")
        for detail in case.get("failure_contains", []):
            if detail not in test["failure"]:
                errors.append(f"Baseline target lacks expected detail in {job['name']}: {case['name']}")
    for case in controls:
        if tests[case["name"]]["outcome"] != "passed":
            errors.append(f"Baseline control did not pass in {job['name']}: {case['name']}")


def compare_test_job(before, after, target_names, intermittent_classes, errors,
                     unexpected_passes, new_tests, inconclusive_changes):
    old = tests_by_identity(before)
    current = tests_by_identity(after)
    missing = sorted(old.keys() - current.keys())
    if missing:
        errors.append(f"Candidate is missing tests in {before['name']}: {missing}")
    for identity, previous in old.items():
        candidate = current.get(identity)
        if candidate is None:
            continue
        name = previous["name"]
        if candidate["name"] != name:
            errors.append(f"Rendered test name changed in {before['name']}: {identity}")
        if candidate["class_name"] != previous["class_name"]:
            errors.append(f"Test class changed in {before['name']}: {name}")
        if (candidate["outcome"] != previous["outcome"]
                and candidate["class_name"] in intermittent_classes
                and {previous["outcome"], candidate["outcome"]} == {"passed", "failed"}):
            inconclusive_changes.append({
                "job": before["name"], "name": name,
                "class_name": candidate["class_name"],
                "baseline_outcome": previous["outcome"],
                "candidate_outcome": candidate["outcome"],
            })
        elif name in target_names:
            if candidate["outcome"] != "passed":
                errors.append(f"Target did not pass in {before['name']}: {name}")
        elif previous["outcome"] == "failed" and candidate["outcome"] == "passed":
            unexpected_passes.append(f"{before['name']} :: {name}")
        elif candidate["outcome"] != previous["outcome"]:
            errors.append(f"Outcome changed {previous['outcome']} -> {candidate['outcome']} "
                          f"in {before['name']}: {name}")
        elif (previous["outcome"] == "failed"
              and candidate["failure_classification"] != previous["failure_classification"]):
            errors.append(f"Failure classification changed in {before['name']}: {name}")
    for identity in sorted(current.keys() - old.keys()):
        name = current[identity]["name"]
        if current[identity]["outcome"] != "passed":
            errors.append(f"New candidate test does not pass in {before['name']}: {name}")
        else:
            new_tests.append(f"{before['name']} :: {name}")


def compare(baseline, candidate, regressions, controls, expected_target_jobs,
            intermittent_classes):
    before_evidence, before_jobs = baseline
    after_evidence, after_jobs = candidate
    errors, blockers, unexpected_passes, new_tests, inconclusive_changes = [], [], [], [], []
    if before_evidence["evidence_definition_sha"] != after_evidence["evidence_definition_sha"]:
        errors.append("Baseline and candidate use different evidence definitions")
    if before_evidence["run"]["id"] == after_evidence["run"]["id"]:
        errors.append("Baseline and candidate refer to the same run")
    if set(before_jobs) != set(after_jobs):
        errors.append(f"Job matrix changed: missing={sorted(before_jobs.keys() - after_jobs.keys())}; "
                      f"extra={sorted(after_jobs.keys() - before_jobs.keys())}")
    target_names = {case["name"] for case in regressions + controls}
    target_jobs = []
    known_failures = []
    architecture_limitations = []
    for name in sorted(before_jobs.keys() & after_jobs.keys()):
        before, after = before_jobs[name], after_jobs[name]
        if before["kind"] != after["kind"]:
            errors.append(f"Job kind changed: {name}")
            continue
        if before["kind"] == "tests":
            if before.get("runtime_architecture") != after.get("runtime_architecture"):
                errors.append(f"Runtime architecture changed between runs: {name}")
            for label, job in (("baseline", before), ("candidate", after)):
                if job.get("architecture_verified") is False:
                    runtime = job.get("runtime_architecture")
                    if runtime == "unmeasured":
                        architecture_limitations.append(
                            f"{label} {name} claims {job.get('claimed_architecture')} but "
                            "runtime architecture was not measured")
                    else:
                        architecture_limitations.append(
                            f"{label} {name} claims {job.get('claimed_architecture')} but ran "
                            f"{runtime}")
            present = set(target_tests(before, target_names))
            candidate_present = set(target_tests(after, target_names))
            if present and present != target_names:
                errors.append(f"Baseline has partial target coverage in {name}")
            if candidate_present != present:
                errors.append(f"Target coverage changed in {name}")
            if present == target_names:
                target_jobs.append(name)
                verify_baseline_target(before, regressions, controls, errors)
            compare_test_job(before, after, target_names, intermittent_classes, errors,
                             unexpected_passes, new_tests, inconclusive_changes)
            known_failures.extend(
                f"{name} :: {test['name']}" for test in after["tests"]
                if test["outcome"] == "failed"
            )
        elif before["kind"] == "repro":
            for label, job in (("baseline", before), ("candidate", after)):
                if job["verdict"] in ("harness_error", "inconclusive"):
                    detail = job.get("diagnostics") or [job["classification"]]
                    blockers.append(f"{label} repro is {job['verdict']}: {name}: {detail[0]}")
            if (before["verdict"], before["classification"]) != (
                    after["verdict"], after["classification"]):
                errors.append(f"Repro outcome changed without review: {name}")
        elif after["conclusion"] != "success":
            errors.append(f"Candidate required check failed: {name}")
    if len(target_jobs) != expected_target_jobs:
        errors.append(f"Expected target coverage in {expected_target_jobs} jobs, found {len(target_jobs)}")
    return {
        "accepted": not errors and not blockers and not unexpected_passes
                    and not inconclusive_changes,
        "outcome": "behavior_correct" if not errors and not blockers and not unexpected_passes
                   and not inconclusive_changes
                   else "inconclusive",
        "errors": errors,
        "blockers": blockers,
        "inconclusive_changes": inconclusive_changes,
        "unexpected_passes": unexpected_passes,
        "new_passing_tests": new_tests,
        "target_jobs": target_jobs,
        "known_failures": known_failures,
        "coverage_gaps": before_evidence["coverage_gaps"],
        "architecture_limitations": architecture_limitations,
    }


def parser():
    arguments = argparse.ArgumentParser(description=__doc__)
    arguments.add_argument("--baseline", required=True)
    arguments.add_argument("--candidate", required=True)
    arguments.add_argument("--manifest", default="scripts/bugfix/issues.json")
    arguments.add_argument("--issue", required=True, type=int)
    arguments.add_argument("--base-sha", required=True)
    arguments.add_argument("--candidate-sha", required=True)
    arguments.add_argument("--workflow-path", default=".github/workflows/bugfix-full-ci.yml")
    arguments.add_argument("--quarantine", default=".github/bugfix/full-ci-quarantine.json")
    arguments.add_argument("--failure-normalization",
                           default="scripts/bugfix/failure-normalization.json")
    arguments.add_argument("--baseline-policy",
                           default="scripts/bugfix/known-failure-classes.json")
    arguments.add_argument("--expected-target-job-count", type=int, default=21)
    arguments.add_argument("--output", required=True)
    return arguments


def main(argv=None):
    args = parser().parse_args(argv)
    try:
        require(args.expected_target_job_count > 0, "Expected target job count must be positive")
        base_sha = require_sha(args.base_sha, "base_sha")
        candidate_sha = require_sha(args.candidate_sha, "candidate_sha")
        require(base_sha != candidate_sha, "Baseline and candidate commits must differ")
        quarantine = load_quarantine(args.quarantine)
        failure_normalization_sha = hashlib.sha256(
            Path(args.failure_normalization).read_bytes()).hexdigest()
        regressions, controls = load_contract(args.manifest, args.issue)
        intermittent_classes, baseline_policy_sha = load_intermittent_classes(
            args.baseline_policy)
        baseline = load_evidence(args.baseline, "baseline", args.issue, base_sha,
                                 quarantine, args.workflow_path, failure_normalization_sha)
        candidate = load_evidence(args.candidate, "candidate", args.issue, candidate_sha,
                                  quarantine, args.workflow_path, failure_normalization_sha)
        report = compare(baseline, candidate, regressions, controls,
                         args.expected_target_job_count, intermittent_classes)
        report["provenance"] = {
            "issue": args.issue,
            "baseline_run_id": baseline[0]["run"]["id"],
            "candidate_run_id": candidate[0]["run"]["id"],
            "base_sha": base_sha,
            "candidate_sha": candidate_sha,
            "evidence_definition_sha": baseline[0]["evidence_definition_sha"],
            "workflow_path": args.workflow_path,
            "quarantine_sha256": quarantine["sha256"],
            "failure_normalization_sha256": failure_normalization_sha,
            "baseline_policy_sha256": baseline_policy_sha,
        }
    except (EvidenceError, OSError, ValueError, KeyError, TypeError) as error:
        report = {"accepted": False, "outcome": "harness_error", "errors": [str(error)]}
    report["schema_version"] = 1
    output = Path(args.output)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(json.dumps(report))
    return 0 if report["accepted"] else 1


if __name__ == "__main__":
    sys.exit(main())
