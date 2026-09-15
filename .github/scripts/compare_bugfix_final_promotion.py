#!/usr/bin/env python3
"""Verify one final full matrix against every permanently accepted bugfix."""

import argparse
from collections import defaultdict
import hashlib
import json
from pathlib import Path
import re
import subprocess
import sys

from apply_issue_2794_harness_overlay import (
    load_manifest_path as load_issue_2794_overlay,
    provenance_contract as issue_2794_contract,
)
from apply_issue_2825_harness_overlay import (
    load_manifest_path as load_issue_2825_overlay,
    provenance_contract as issue_2825_contract,
)
from bugfix_full_ci_quarantine import load_quarantine
from compare_bugfix_full_ci import (
    load_baseline_policy,
    overlay_summary,
    target_tests,
    tests_by_identity,
    validate_job,
)
sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "scripts/bugfix"))
from source_context import consume as consume_source_context, final_observations


SHA = re.compile(r"[0-9a-f]{40}\Z")
SHA256 = re.compile(r"[0-9a-f]{64}\Z")
REPRO = re.compile(r"repro-runner / Run (Issue_(\d+)(?:_[A-Za-z0-9_]+)?) on ")
OVERLAY_REPROS = {
    2794: (re.compile(r"repro-runner / Run Issue_2794_SharedJobHandoff on "
                      r"(ubuntu-22\.04|ubuntu-24\.04|windows-2022)"),
           issue_2794_contract),
    2825: (re.compile(r"repro-runner / Run Issue_2825_FreeListRace on "
                      r"(ubuntu-22\.04|ubuntu-24\.04|windows-2022)"),
           issue_2825_contract),
}
EXPECTED_JOBS = 109
EXPECTED_ARTIFACTS = 105
EXPECTED_ORDINARY_TEST_JOBS = 21
EXPECTED_REPRO_PLATFORMS = 3
IMMUTABLE_TEST_PATHS = ("LiteDB.Tests", "LiteDB.ReproRunner", "tests.runsettings")


class PromotionError(ValueError):
    """Final-promotion evidence is malformed or incomplete."""


def require(condition, message):
    if not condition:
        raise PromotionError(message)


def canonical_digest(value):
    encoded = json.dumps(value, sort_keys=True, separators=(",", ":"),
                         ensure_ascii=False).encode("utf-8")
    return hashlib.sha256(encoded).hexdigest()


def read_object(path, maximum_bytes=128 * 1024 * 1024):
    raw = Path(path).read_bytes()
    require(0 < len(raw) <= maximum_bytes, f"JSON input exceeds bound: {path}")
    value = json.loads(raw)
    require(isinstance(value, dict), f"Expected a JSON object in {path}")
    return value


def git(repository, *arguments):
    return subprocess.run(["git", "-C", str(repository), *arguments], check=True,
                          capture_output=True).stdout.decode("utf-8").strip()


def verify_commit(repository, sha, label):
    require(isinstance(sha, str) and SHA.fullmatch(sha),
            f"{label} must be a full lowercase commit SHA")
    require(git(repository, "rev-parse", "--verify", sha + "^{commit}") == sha,
            f"{label} commit is unavailable")


def verify_ledger_source(repository, state_sha, ledger_path):
    verify_commit(repository, state_sha, "accepted state")
    entry = git(repository, "ls-tree", state_sha, "--", "accepted-tests.json")
    require(entry and entry.split()[0] == "100644",
            "Accepted state has no ordinary accepted-tests.json blob")
    result = subprocess.run(
        ["git", "-C", str(repository), "show", f"{state_sha}:accepted-tests.json"],
        capture_output=True, check=False)
    require(result.returncode == 0
            and result.stdout == Path(ledger_path).read_bytes(),
            "Accepted ledger bytes do not come from the exact state commit")


def load_accepted_contracts(repository, ledger_path, manifest_path, base_sha,
                            final_sha, test_source_sha, expected_ledger_sha):
    for value, label in ((base_sha, "base"), (final_sha, "final integration"),
                         (test_source_sha, "test source")):
        verify_commit(repository, value, label)
    require(base_sha == test_source_sha,
            "Final promotion must use the original frozen baseline as test source")
    require(git(repository, "merge-base", base_sha, final_sha) == base_sha,
            "Final integration commit does not descend from the frozen baseline")
    immutable_test_sources = {}
    for path in IMMUTABLE_TEST_PATHS:
        baseline_object = git(repository, "rev-parse", f"{test_source_sha}:{path}")
        require(git(repository, "rev-parse", f"{final_sha}:{path}") == baseline_object,
                f"Final integration changed frozen test/harness source: {path}")
        immutable_test_sources[path] = baseline_object
    ledger = read_object(ledger_path, 8 * 1024 * 1024)
    require(ledger.get("schema_version") == 1
            and isinstance(ledger.get("issues"), dict) and ledger["issues"],
            "Accepted ledger has no valid issue contracts")
    require(SHA256.fullmatch(expected_ledger_sha or "")
            and canonical_digest(ledger) == expected_ledger_sha,
            "Accepted ledger digest changed")
    manifest_raw = Path(manifest_path).read_bytes()
    require(0 < len(manifest_raw) <= 8 * 1024 * 1024,
            "Trusted issue manifest exceeds bound")
    manifest = json.loads(manifest_raw)
    require(manifest.get("schema_version") == 1
            and isinstance(manifest.get("issues"), dict),
            "Invalid trusted issue manifest")
    accepted = {}
    all_names = set()
    frozen_paths = {}
    for issue_text, entry in sorted(ledger["issues"].items(), key=lambda item: int(item[0])):
        require(isinstance(issue_text, str) and issue_text.isdecimal()
                and int(issue_text) > 0 and isinstance(entry, dict),
                "Invalid accepted issue entry")
        issue = int(issue_text)
        contract = manifest["issues"].get(issue_text)
        require(isinstance(contract, dict)
                and contract.get("inventory_issue") == issue,
                f"Accepted issue {issue} lacks a trusted execution contract")
        require(contract.get("frozen_test_revision") == test_source_sha
                and entry.get("test_source_sha") == test_source_sha,
                f"Accepted issue {issue} uses a different regression source")
        candidate_sha = entry.get("candidate_sha")
        verify_commit(repository, candidate_sha, f"accepted issue {issue} candidate")
        entry_base_sha = entry.get("base_sha")
        verify_commit(repository, entry_base_sha, f"accepted issue {issue} base")
        require(entry_base_sha != candidate_sha
                and git(repository, "merge-base", entry_base_sha,
                        candidate_sha) == entry_base_sha,
                f"Accepted issue {issue} candidate does not descend from its recorded base")
        require(git(repository, "merge-base", candidate_sha, final_sha) == candidate_sha,
                f"Accepted issue {issue} is absent from the final integration history")
        if "candidate_tree_sha" in entry:
            require(entry["candidate_tree_sha"] == git(
                repository, "rev-parse", candidate_sha + "^{tree}"),
                f"Accepted issue {issue} candidate tree identity changed")
        regressions = contract.get("regressions")
        controls = contract.get("controls")
        require(isinstance(regressions, list) and regressions
                and isinstance(controls, list) and controls,
                f"Accepted issue {issue} has an incomplete test contract")
        cases = regressions + controls
        names = [case.get("name") for case in cases]
        require(all(isinstance(name, str) and name.startswith("LiteDB.")
                    for name in names) and len(names) == len(set(names)),
                f"Accepted issue {issue} has invalid test identities")
        require(entry.get("tests") == sorted(names),
                f"Accepted issue {issue} ledger tests differ from its trusted contract")
        require(not (all_names & set(names)),
                f"Accepted issue {issue} repeats another issue's test identity")
        all_names.update(names)
        blobs = contract.get("frozen_test_blobs")
        require(isinstance(blobs, dict) and blobs,
                f"Accepted issue {issue} has no frozen test blobs")
        for path, expected_blob in blobs.items():
            require(isinstance(path, str) and path.startswith("LiteDB.Tests/")
                    and isinstance(expected_blob, str)
                    and re.fullmatch(r"[0-9a-f]{40}", expected_blob),
                    f"Accepted issue {issue} has an invalid frozen test blob")
            require(git(repository, "rev-parse", f"{test_source_sha}:{path}") == expected_blob,
                    f"Frozen source blob changed for accepted issue {issue}: {path}")
            require(git(repository, "rev-parse", f"{final_sha}:{path}") == expected_blob,
                    f"Final integration changed a frozen test for issue {issue}: {path}")
            frozen_paths[path] = expected_blob
        repros = contract.get("full_matrix_repros", [])
        require(isinstance(repros, list)
                and all(isinstance(item, dict)
                        and set(item) == {"repro", "role"}
                        and re.fullmatch(rf"Issue_{issue}(?:_[A-Za-z0-9_]+)?",
                                         item["repro"] or "")
                        and item["role"] in ("regression", "control")
                        for item in repros),
                f"Accepted issue {issue} has invalid full-matrix repro roles")
        require(len({item["repro"] for item in repros}) == len(repros),
                f"Accepted issue {issue} repeats a full-matrix repro role")
        accepted[issue] = {"entry": entry, "regressions": regressions,
                           "controls": controls, "contract": contract,
                           "repro_roles": {item["repro"]: item["role"]
                                           for item in repros}}
    by_base = defaultdict(list)
    for issue, value in accepted.items():
        by_base[value["entry"]["base_sha"]].append(issue)
    consumed = []
    current = base_sha
    while current != final_sha:
        next_issues = by_base.get(current, [])
        require(len(next_issues) == 1,
                "Accepted ledger does not form one contiguous integration chain")
        issue = next_issues[0]
        require(issue not in consumed,
                "Accepted ledger integration chain contains a cycle")
        consumed.append(issue)
        current = accepted[issue]["entry"]["candidate_sha"]
    require(set(consumed) == set(accepted),
            "Accepted ledger has unused entries or does not end at the final integration SHA")
    return (accepted, ledger, hashlib.sha256(manifest_raw).hexdigest(),
            frozen_paths, immutable_test_sources)


def load_evidence(path, label, expected_sha, validation, quarantine,
                  normalization_sha, overlays, workflow_path):
    evidence = read_object(path)
    require(evidence.get("schema_version") == 1 and evidence.get("accepted") is True,
            f"{label} collection was not accepted")
    require(evidence.get("validation") == validation,
            f"{label} final-promotion identity changed")
    require(evidence.get("source_sha") == expected_sha
            and evidence.get("role") == label,
            f"{label} collection tested the wrong source or role")
    require(evidence.get("failure_normalization_sha256") == normalization_sha,
            f"{label} used a different failure-normalization policy")
    require(evidence.get("quarantine_sha256") == quarantine["sha256"]
            and evidence.get("coverage_gaps") == quarantine["coverage_gaps"],
            f"{label} used a different quarantine contract")
    definition_sha = evidence.get("evidence_definition_sha")
    require(isinstance(definition_sha, str) and SHA.fullmatch(definition_sha),
            f"{label} lacks an immutable evidence definition")
    for issue, details in overlays.items():
        prefix = "harness_overlay" if issue == 2794 else "issue_2825_harness_overlay"
        require(evidence.get(prefix + "_manifest_sha256") == details["sha256"],
                f"{label} used a different #{issue} overlay manifest")
        require(evidence.get(prefix) == overlay_summary(
            details["manifest"], details["sha256"], definition_sha),
            f"{label} #{issue} overlay identity is incomplete")
    run = evidence.get("run")
    require(isinstance(run, dict) and type(run.get("id")) is int and run["id"] > 0,
            f"{label} run identity is invalid")
    require(run.get("head_sha") == definition_sha
            and run.get("workflow_path") == workflow_path
            and run.get("event") == "workflow_dispatch"
            and run.get("status") == "completed"
            and run.get("conclusion") in ("success", "failure"),
            f"{label} run provenance is invalid")
    jobs = evidence.get("jobs")
    require(isinstance(jobs, list) and len(jobs) == EXPECTED_JOBS,
            f"{label} must contain exactly {EXPECTED_JOBS} jobs")
    names = [validate_job(job) for job in jobs]
    require(len(names) == len(set(names)), f"{label} repeats a job identity")
    require(not (quarantine["jobs"] & set(names)),
            f"{label} executed a quarantined job")
    artifact_jobs = [job for job in jobs if job["kind"] in ("tests", "repro")]
    require(len(artifact_jobs) == EXPECTED_ARTIFACTS,
            f"{label} must authenticate exactly {EXPECTED_ARTIFACTS} evidence artifacts")
    artifact_ids = set()
    for job in artifact_jobs:
        require(type(job.get("artifact_id")) is int and job["artifact_id"] > 0
                and isinstance(job.get("artifact_sha256"), str)
                and SHA256.fullmatch(job["artifact_sha256"]),
                f"{label} job lacks an authenticated artifact: {job['name']}")
        require(job["artifact_id"] not in artifact_ids,
                f"{label} reuses an artifact ID")
        artifact_ids.add(job["artifact_id"])
    observed_overlay_os = defaultdict(set)
    for job in jobs:
        matched_issue = None
        for issue, (pattern, contract) in OVERLAY_REPROS.items():
            match = pattern.fullmatch(job["name"])
            if match:
                matched_issue = issue
                expected = contract(overlays[issue]["manifest"], overlays[issue]["sha256"],
                                    expected_sha, definition_sha, match.group(1))
                require(job.get("harness_overlay") == expected,
                        f"{label} #{issue} job has untrusted overlay provenance")
                observed_overlay_os[issue].add(match.group(1))
                break
        if matched_issue is None:
            require("harness_overlay" not in job,
                    f"{label} applied a harness overlay to another job")
    expected_os = {"ubuntu-22.04", "ubuntu-24.04", "windows-2022"}
    require(all(observed_overlay_os[issue] == expected_os for issue in OVERLAY_REPROS),
            f"{label} does not contain all overlaid repro jobs")
    expected_conclusion = "failure" if any(job["conclusion"] == "failure" for job in jobs) else "success"
    require(run["conclusion"] == expected_conclusion,
            f"{label} run conclusion disagrees with its jobs")
    return evidence, {job["name"]: job for job in jobs}


def baseline_target_contract(job, accepted, errors):
    regressions = [case for value in accepted.values() for case in value["regressions"]]
    controls = [case for value in accepted.values() for case in value["controls"]]
    names = {case["name"] for case in regressions + controls}
    tests = target_tests(job, names)
    require(set(tests) == names, f"Partial accepted-test coverage in {job['name']}")
    for case in regressions:
        result = tests[case["name"]]
        if result["outcome"] != "failed":
            errors.append(f"Accepted regression was not red in baseline {job['name']}: {case['name']}")
            continue
        failure = result["failure"]
        lines = failure.replace("\r\n", "\n").splitlines()
        if not lines or lines[0] != case.get("failure_first_line"):
            errors.append(f"Accepted regression had wrong baseline defect in {job['name']}: {case['name']}")
        for detail in case.get("failure_contains", []):
            if detail not in failure:
                errors.append(f"Accepted regression lacked baseline detail in {job['name']}: {case['name']}")
    for case in controls:
        if tests[case["name"]]["outcome"] != "passed":
            errors.append(f"Accepted control was not green in baseline {job['name']}: {case['name']}")


def compare_test_job(before, after, target_names, intermittent_classes, errors,
                     unexpected_passes, inconclusive, observations=(), source_changes=None):
    old, current = tests_by_identity(before), tests_by_identity(after)
    consumed = []
    if set(old) != set(current):
        errors.append(f"Frozen test identities changed in {before['name']}: "
                      f"missing={sorted(old.keys() - current.keys())}; "
                      f"extra={sorted(current.keys() - old.keys())}")
    for identity in sorted(old.keys() & current.keys()):
        previous, candidate = old[identity], current[identity]
        name = previous["name"]
        if candidate["name"] != name or candidate["class_name"] != previous["class_name"]:
            errors.append(f"Frozen test rendering changed in {before['name']}: {identity}")
            continue
        if name in target_names:
            if candidate["outcome"] != "passed":
                errors.append(f"Accepted test did not pass in {before['name']}: {name}")
            continue
        if (candidate["outcome"] != previous["outcome"]
                and candidate["class_name"] in intermittent_classes
                and {previous["outcome"], candidate["outcome"]} == {"passed", "failed"}):
            inconclusive.append({"job": before["name"], "name": name,
                                 "class_name": candidate["class_name"],
                                 "baseline_outcome": previous["outcome"],
                                 "candidate_outcome": candidate["outcome"]})
        elif previous["outcome"] == "failed" and candidate["outcome"] == "passed":
            observation = consume_source_context(previous, candidate, observations)
            if observation is None:
                unexpected_passes.append(f"{before['name']} :: {name}")
            else:
                consumed.append(observation["test_name"])
                source_changes.append({"job": before["name"], **observation})
        elif candidate["outcome"] != previous["outcome"]:
            errors.append(f"Outcome changed {previous['outcome']} -> {candidate['outcome']} "
                          f"in {before['name']}: {name}")
        elif (previous["outcome"] == "failed"
              and candidate["failure_classification"] != previous["failure_classification"]):
            errors.append(f"Failure classification changed in {before['name']}: {name}")
    observed_names = {item["test_name"] for item in observations}
    present = {item["name"] for item in old.values()} | {item["name"] for item in current.values()}
    if observed_names & present != set(consumed):
        errors.append(f"Recorded source observation did not match completed frozen guard transitions in {before['name']}")


def required_environments(accepted):
    required = set()
    for issue, value in accepted.items():
        environments = value["contract"].get("required_environments", [])
        require(isinstance(environments, list)
                and all(isinstance(item, str) for item in environments),
                f"Issue {issue} has invalid required environments")
        for environment in environments:
            required.add((issue, environment))
    return required


def proven_environment(job):
    if job.get("architecture_verified") is not True:
        return None
    name = job["name"]
    os_name = "linux" if "Linux" in name else (
        "windows" if "Windows" in name else ("macos" if "macOS" in name else None))
    framework = "net10.0" if ".NET 10" in name else (
        "net8.0" if ".NET 8" in name or ".NET 9" in name else None)
    runtime = job.get("runtime_architecture")
    architecture = "x64" if runtime in ("AMD64", "x86_64") else (
        "arm64" if runtime in ("arm64", "aarch64") else (
            "x86" if runtime == "x86" else None))
    return f"{os_name}-{architecture}-{framework}" \
        if os_name and architecture and framework else None


def compare(baseline, candidate, accepted, allowed_classes, allowed_skips,
            intermittent_classes, source_observations=()):
    before_evidence, before_jobs = baseline
    after_evidence, after_jobs = candidate
    errors, blockers, unexpected_passes, inconclusive = [], [], [], []
    if before_evidence["evidence_definition_sha"] != after_evidence["evidence_definition_sha"]:
        errors.append("Baseline and candidate used different evidence definitions")
    if before_evidence["run"]["id"] == after_evidence["run"]["id"]:
        errors.append("Baseline and candidate refer to the same run")
    if set(before_jobs) != set(after_jobs):
        errors.append("Baseline and candidate job matrices differ")
    target_names = {case["name"] for value in accepted.values()
                    for case in value["regressions"] + value["controls"]}
    target_jobs, known_failures, repro_transitions = [], [], []
    source_changes = []
    architecture_limitations, proven_environments = [], set()
    observed_declared_repros = defaultdict(set)
    accepted_issues = set(accepted)
    for name in sorted(before_jobs.keys() & after_jobs.keys()):
        before, after = before_jobs[name], after_jobs[name]
        if before["kind"] != after["kind"]:
            errors.append(f"Job kind changed: {name}")
            continue
        if before["kind"] == "tests":
            for result in before["tests"]:
                if result["outcome"] == "failed" and result["class_name"] not in allowed_classes:
                    errors.append(f"Unclassified baseline failure in {name}: {result['name']}")
                if result["outcome"] == "skipped" and result["name"] not in allowed_skips:
                    errors.append(f"Unclassified baseline skip in {name}: {result['name']}")
            if before.get("runtime_architecture") != after.get("runtime_architecture"):
                errors.append(f"Runtime architecture changed between runs: {name}")
            for label, job in (("baseline", before), ("candidate", after)):
                claimed, runtime = job.get("claimed_architecture"), job.get("runtime_architecture")
                if job.get("architecture_verified") is False:
                    architecture_limitations.append(
                        f"{label} {name} claims {claimed} but ran {runtime}")
                elif job.get("architecture_verified") is True:
                    environment = proven_environment(job)
                    if environment:
                        proven_environments.add(environment)
            present = set(target_tests(before, target_names))
            current = set(target_tests(after, target_names))
            if present or current:
                if present != target_names or current != target_names:
                    errors.append(f"Partial accepted-test coverage in {name}")
                else:
                    target_jobs.append(name)
                    baseline_target_contract(before, accepted, errors)
            compare_test_job(before, after, target_names, intermittent_classes,
                             errors, unexpected_passes, inconclusive, source_observations, source_changes)
            known_failures.extend(f"{name} :: {test['name']}" for test in after["tests"]
                                  if test["outcome"] == "failed")
        elif before["kind"] == "repro":
            for label, job in (("baseline", before), ("candidate", after)):
                if job["verdict"] in ("harness_error", "inconclusive"):
                    detail = (job.get("diagnostics") or [job["classification"]])[0]
                    blockers.append(f"{label} repro is {job['verdict']}: {name}: {detail}")
            match = REPRO.match(name)
            repro_name = match.group(1) if match else None
            issue = int(match.group(2)) if match else None
            changed = (before["verdict"], before["classification"]) != (
                after["verdict"], after["classification"])
            if issue in accepted_issues:
                role = accepted[issue]["repro_roles"].get(repro_name)
                if role is None:
                    errors.append(f"Accepted issue repro has no explicit regression/control role: {name}")
                else:
                    observed_declared_repros[(issue, repro_name)].add(name)
                    if role == "regression" and (before["verdict"], after["verdict"]) != (
                            "bug_present", "behavior_correct"):
                        errors.append(f"Accepted regression repro lacks bug_present -> behavior_correct proof: {name}")
                    elif role == "regression" and not changed:
                        errors.append(f"Accepted regression repro transition lacks distinct evidence: {name}")
                    elif role == "regression":
                        repro_transitions.append(name)
                    elif (before["verdict"], after["verdict"]) != (
                            "behavior_correct", "behavior_correct") or changed:
                        errors.append(f"Accepted control repro did not remain exactly behavior_correct: {name}")
            elif changed:
                errors.append(f"Nonaccepted repro outcome changed: {name}")
        elif after["conclusion"] != "success":
            errors.append(f"Candidate required check failed: {name}")
    if len(target_jobs) != EXPECTED_ORDINARY_TEST_JOBS:
        errors.append(f"Expected every accepted test in {EXPECTED_ORDINARY_TEST_JOBS} ordinary jobs; "
                      f"found {len(target_jobs)}")
    for issue, value in accepted.items():
        for repro_name in value["repro_roles"]:
            count = len(observed_declared_repros[(issue, repro_name)])
            if count != EXPECTED_REPRO_PLATFORMS:
                errors.append(f"Accepted repro {repro_name} ran in {count} of "
                              f"{EXPECTED_REPRO_PLATFORMS} required jobs")
    required_arch = required_environments(accepted)
    for issue, environment in sorted(required_arch):
        if environment not in proven_environments:
            blockers.append(f"Accepted issue {issue} requires unproven {environment} execution")
    for observation in source_observations:
        observed_jobs = {change["job"] for change in source_changes
                         if change["test_name"] == observation["test_name"]}
        for job in sorted(set(target_jobs) - observed_jobs):
            errors.append(f"Source guard observation is missing from required ordinary job {job}: {observation['test_name']}")
    accepted_result = not errors and not blockers and not unexpected_passes and not inconclusive
    return {
        "schema_version": 1,
        "accepted": accepted_result,
        "outcome": "promotion_ready" if accepted_result else "inconclusive",
        "errors": errors,
        "blockers": blockers,
        "unexpected_passes": unexpected_passes,
        "inconclusive_changes": inconclusive,
        "accepted_issues": sorted(accepted),
        "accepted_tests": sorted(target_names),
        "accepted_test_case_count": len(target_names),
        "accepted_test_execution_count": len(target_names) * len(target_jobs),
        "accepted_test_jobs": target_jobs,
        "accepted_repro_transitions": repro_transitions,
        "source_context_changes": source_changes,
        "remaining_known_failures": known_failures,
        "coverage_gaps": before_evidence["coverage_gaps"],
        "architecture_limitations": sorted(set(architecture_limitations)),
        "artifact_counts": {"baseline": EXPECTED_ARTIFACTS,
                            "candidate": EXPECTED_ARTIFACTS},
    }


def parser():
    arguments = argparse.ArgumentParser(description=__doc__)
    arguments.add_argument("--repository", type=Path, required=True)
    arguments.add_argument("--baseline", required=True)
    arguments.add_argument("--candidate", required=True)
    arguments.add_argument("--accepted-ledger", required=True)
    arguments.add_argument("--accepted-state-sha", required=True)
    arguments.add_argument("--accepted-ledger-sha256", required=True)
    arguments.add_argument("--manifest", default="scripts/bugfix/issues.json")
    arguments.add_argument("--base-sha", required=True)
    arguments.add_argument("--final-integration-sha", required=True)
    arguments.add_argument("--test-source-sha", required=True)
    arguments.add_argument("--workflow-path", default=".github/workflows/bugfix-full-ci.yml")
    arguments.add_argument("--quarantine", default=".github/bugfix/full-ci-quarantine.json")
    arguments.add_argument("--failure-normalization", default="scripts/bugfix/failure-normalization.json")
    arguments.add_argument("--baseline-policy", default="scripts/bugfix/known-failure-classes.json")
    arguments.add_argument("--harness-overlay-manifest", default=".github/bugfix/issue-2794-harness-overlay.json")
    arguments.add_argument("--issue-2825-harness-overlay-manifest", default=".github/bugfix/issue-2825-harness-overlay.json")
    arguments.add_argument("--grading-policy-sha", required=True)
    arguments.add_argument("--output", required=True)
    return arguments


def main(argv=None):
    args = parser().parse_args(argv)
    report = None
    try:
        require(SHA.fullmatch(args.accepted_state_sha or ""),
                "accepted_state_sha must be a full lowercase commit SHA")
        require(SHA.fullmatch(args.grading_policy_sha or ""),
                "grading_policy_sha must be a full lowercase commit SHA")
        verify_ledger_source(args.repository.resolve(), args.accepted_state_sha,
                             args.accepted_ledger)
        accepted, ledger, manifest_sha, frozen_paths, immutable_test_sources = \
            load_accepted_contracts(
            args.repository.resolve(), args.accepted_ledger, args.manifest,
            args.base_sha, args.final_integration_sha, args.test_source_sha,
            args.accepted_ledger_sha256)
        quarantine = load_quarantine(args.quarantine)
        normalization_raw = Path(args.failure_normalization).read_bytes()
        normalization_sha = hashlib.sha256(normalization_raw).hexdigest()
        classes, skips, intermittent, baseline_policy_sha = load_baseline_policy(
            args.baseline_policy)
        baseline_policy = read_object(args.baseline_policy, 8 * 1024 * 1024)
        require(baseline_policy.get("source_revision") == args.base_sha,
                "Known-failure policy is for a different frozen baseline")
        overlay_2794, overlay_2794_sha = load_issue_2794_overlay(
            args.harness_overlay_manifest)
        overlay_2825, overlay_2825_sha = load_issue_2825_overlay(
            args.issue_2825_harness_overlay_manifest)
        overlays = {
            2794: {"manifest": overlay_2794, "sha256": overlay_2794_sha},
            2825: {"manifest": overlay_2825, "sha256": overlay_2825_sha},
        }
        validation = {
            "kind": "final-promotion",
            "final_integration_sha": args.final_integration_sha,
            "accepted_state_sha": args.accepted_state_sha,
            "accepted_ledger_sha256": args.accepted_ledger_sha256,
            "test_source_sha": args.test_source_sha,
        }
        baseline = load_evidence(args.baseline, "baseline", args.base_sha,
                                 validation, quarantine, normalization_sha,
                                 overlays, args.workflow_path)
        candidate = load_evidence(args.candidate, "candidate",
                                  args.final_integration_sha, validation,
                                  quarantine, normalization_sha, overlays,
                                  args.workflow_path)
        observations = final_observations(args.repository, accepted, args.final_integration_sha)
        report = compare(baseline, candidate, accepted, classes, skips, intermittent, observations)
        report["provenance"] = {
            "base_sha": args.base_sha,
            "final_integration_sha": args.final_integration_sha,
            "final_integration_tree_sha": git(
                args.repository, "rev-parse", args.final_integration_sha + "^{tree}"),
            "accepted_state_sha": args.accepted_state_sha,
            "accepted_ledger_sha256": args.accepted_ledger_sha256,
            "accepted_ledger_raw_sha256": hashlib.sha256(
                Path(args.accepted_ledger).read_bytes()).hexdigest(),
            "test_source_sha": args.test_source_sha,
            "frozen_test_blobs": frozen_paths,
            "immutable_test_sources": immutable_test_sources,
            "issue_manifest_sha256": manifest_sha,
            "baseline_run_id": baseline[0]["run"]["id"],
            "candidate_run_id": candidate[0]["run"]["id"],
            "evidence_definition_sha": baseline[0]["evidence_definition_sha"],
            "grading_policy_sha": args.grading_policy_sha,
            "workflow_path": args.workflow_path,
            "quarantine_sha256": quarantine["sha256"],
            "failure_normalization_sha256": normalization_sha,
            "baseline_policy_sha256": baseline_policy_sha,
            "harness_overlay_manifest_sha256": overlay_2794_sha,
            "harness_overlay": overlay_summary(
                overlay_2794, overlay_2794_sha,
                baseline[0]["evidence_definition_sha"]),
            "issue_2825_harness_overlay_manifest_sha256": overlay_2825_sha,
            "issue_2825_harness_overlay": overlay_summary(
                overlay_2825, overlay_2825_sha,
                baseline[0]["evidence_definition_sha"]),
        }
    except (PromotionError, ValueError, OSError, KeyError, TypeError,
            subprocess.CalledProcessError) as error:
        report = {"schema_version": 1, "accepted": False,
                  "outcome": "harness_error", "errors": [str(error)],
                  "blockers": [], "unexpected_passes": [],
                  "inconclusive_changes": []}
    output = Path(args.output)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n",
                      encoding="utf-8")
    print(json.dumps(report, ensure_ascii=True))
    return 0 if report["accepted"] else 1


if __name__ == "__main__":
    sys.exit(main())
