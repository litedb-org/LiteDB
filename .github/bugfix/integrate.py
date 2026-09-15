"""Verify and atomically integrate one fully validated correctness fix."""

import argparse
import json
from pathlib import Path
import sys
import tempfile

from evidence import event_for
from integrate_evidence import acceptance_evidence, evidence_manifest, original_matrix_evidence
from integrate_storage import IntegrationStore, LEDGER_NAME, LOCK_NAME, encoded
from patching import git, worktree
from state import NAME, SHA, Rejected, apply_event, require
from storage import github

INTEGRATION_BRANCH = "integration/bugfixes"


def integration_head(repository, repo):
    result = git(repository, "ls-remote", "--heads", f"https://github.com/{repo}.git", f"refs/heads/{INTEGRATION_BRANCH}")
    require(result and len(result.splitlines()) == 1, "Integration branch is missing or ambiguous")
    return result.split()[0]


def tested_tree(repository, repo, state):
    for sha in (state["base_sha"], state["candidate_sha"]):
        require(git(repository, "rev-parse", "--verify", sha + "^{commit}") == sha, "Missing immutable source commit")
    require(git(repository, "merge-base", state["base_sha"], state["candidate_sha"]) == state["base_sha"],
            "Candidate does not descend from the expected integration base")
    tree = git(repository, "rev-parse", state["candidate_sha"] + "^{tree}")
    remote = github(repo, f"git/commits/{state['candidate_sha']}")
    require(remote.get("tree", {}).get("sha") == tree, "Remote candidate tree differs from the tested commit")
    return tree


def advance(repository, repo, state, lock):
    current = integration_head(repository, repo)
    if current == state["candidate_sha"]:
        require(lock["phase"] == "prepared", "Candidate is already integrated without recorded prepared intent")
        return
    require(current == state["base_sha"], "Integration base moved; renew candidate evidence")
    ref = f"refs/heads/{INTEGRATION_BRANCH}"
    git(repository, "push", "--quiet", f"--force-with-lease={ref}:{state['base_sha']}",
        f"https://github.com/{repo}.git", f"{state['candidate_sha']}:{ref}")
    require(integration_head(repository, repo) == state["candidate_sha"], "Integration update readback failed")


def finish(store, state, lock, report, contract, tree):
    latest, sha = store.require_lock(lock["token"])
    require(latest["phase"] == "prepared", "Integration lacks prepared durable evidence")
    current = store.read_at(state["campaign"], sha)
    require(current == state, "Campaign changed during integration")
    ledger = store.read_at(LEDGER_NAME, sha)
    if ledger is None:
        ledger = {"schema_version": 1, "issues": {}}
    require(ledger.get("schema_version") == 1 and isinstance(ledger.get("issues"), dict), "Invalid accepted-test ledger")
    tests = sorted(case["name"] for case in contract["regressions"] + contract["controls"])
    entry = {"campaign": state["campaign"], "base_sha": state["base_sha"], "candidate_sha": state["candidate_sha"],
             "candidate_tree_sha": tree, "test_source_sha": state["test_source_sha"], "tests": tests,
             "matrix_provenance": report["provenance"], "target_jobs": report["target_jobs"],
             "coverage_gaps": report["coverage_gaps"], "architecture_limitations": report.get("architecture_limitations", [])}
    ledger["issues"][str(state["issue"])] = entry
    event = event_for(state, "integrated", report["provenance"]["candidate_run_id"],
                      expected_base_sha=state["base_sha"], integration_sha=state["candidate_sha"],
                      integration_tree_sha=tree, integration_lock=lock["token"], coverage_gaps=report["coverage_gaps"],
                      architecture_limitations=report.get("architecture_limitations", []))
    completed = apply_event(state, event)
    completed["accepted_tests"] = entry
    latest.update(active=False, phase="complete", integration_sha=state["candidate_sha"])
    store.commit(sha, {state["campaign"] + ".json": encoded(completed), LOCK_NAME + ".json": encoded(latest),
                       LEDGER_NAME + ".json": encoded(ledger)},
                 f"Accept bugfix campaign {state['campaign']}\n\nRecord the tested integration commit and permanent passing regression contract.")
    return completed


def execute(args):
    store = IntegrationStore(args.repo)
    state, _ = store.read(args.campaign)
    require(state is not None, "Campaign does not exist")
    require(state["candidate_sha"] == args.candidate_sha, "Explicit candidate differs from campaign state")
    if state["phase"] == "integrated":
        require(integration_head(args.repository, args.repo) == args.candidate_sha, "Integrated campaign branch has since moved")
        return {"phase": "integrated", "candidate_sha": args.candidate_sha, "already_complete": True}
    require(state["phase"] == "ready" and not state["paused"], "Only a ready, unpaused campaign may integrate")
    git(args.repository, "fetch", "--quiet", f"https://github.com/{args.repo}.git", state["base_sha"],
        state["candidate_sha"], state["workflow_sha"], args.evidence_definition_sha, args.grading_policy_sha)
    tree = tested_tree(args.repository, args.repo, state)
    files = acceptance_evidence(args.repo, state)
    with worktree(args.repository, args.grading_policy_sha) as control, tempfile.TemporaryDirectory(prefix="litedb-full-ci-") as directory:
        report, full_files = original_matrix_evidence(args, state, control, Path(directory))
        files.update(full_files)
    raw_manifest = git(args.repository, "show", f"{state['workflow_sha']}:scripts/bugfix/issues.json")
    contract = json.loads(raw_manifest)["issues"][str(state["issue"])]
    require(contract["frozen_test_revision"] == state["test_source_sha"], "Accepted contract changed regression source")
    files["test-contract.json"] = encoded(contract)
    files["evidence-manifest.json"] = encoded(evidence_manifest(files))
    if not args.apply:
        return {"phase": "verified", "candidate_sha": args.candidate_sha, "candidate_tree_sha": tree,
                "evidence_files": len(files), "coverage_gaps": report["coverage_gaps"], "applies": False}
    identity = {"campaign": state["campaign"], "base_sha": state["base_sha"], "candidate_sha": state["candidate_sha"],
                "evidence_definition_sha": args.evidence_definition_sha,
                "grading_policy_sha": args.grading_policy_sha,
                "baseline_run": args.baseline_run, "candidate_run": args.candidate_run}
    lock = store.acquire(identity, resume=args.resume)
    current_head = integration_head(args.repository, args.repo)
    require(current_head == state["base_sha"] or (args.resume and lock["phase"] == "prepared"
            and current_head == state["candidate_sha"]), "Integration branch moved before evidence preparation")
    lock, sha = store.require_lock(lock["token"])
    require(store.read_at(args.campaign, sha) == state, "Campaign changed while integration evidence was validated")
    if lock["phase"] == "acquired":
        lock.update(phase="prepared", candidate_tree_sha=tree, evidence_prefix=f"evidence/{args.campaign}/")
        durable = {f"evidence/{args.campaign}/{name}": data for name, data in files.items()}
        durable[LOCK_NAME + ".json"] = encoded(lock)
        store.commit(sha, durable, f"Preserve validation evidence for {args.campaign}\n\nDurably record acceptance before advancing the integration branch.")
    require(lock["phase"] == "prepared" and lock.get("candidate_tree_sha") == tree, "Prepared integration identity changed")
    advance(args.repository, args.repo, state, lock)
    result = finish(store, state, lock, report, contract, tree)
    return {"phase": result["phase"], "candidate_sha": result["candidate_sha"], "candidate_tree_sha": tree,
            "coverage_gaps": report["coverage_gaps"]}


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", required=True)
    parser.add_argument("--campaign", required=True)
    parser.add_argument("--candidate-sha", required=True)
    parser.add_argument("--baseline-run", type=int, required=True)
    parser.add_argument("--candidate-run", type=int, required=True)
    parser.add_argument("--evidence-definition-sha", required=True)
    parser.add_argument("--grading-policy-sha", required=True, help="Immutable collector/comparator policy commit; distinct from capture workflow SHA")
    parser.add_argument("--repository", type=Path, default=Path.cwd())
    parser.add_argument("--apply", action="store_true", help="Advance integration after all evidence passes; otherwise verify only")
    parser.add_argument("--resume", action="store_true", help="Resume this exact transaction's existing persistent lock")
    args = parser.parse_args(argv)
    require(NAME.fullmatch(args.campaign), "Invalid campaign name")
    require(all(SHA.fullmatch(value) for value in (args.candidate_sha, args.evidence_definition_sha, args.grading_policy_sha)),
            "Full immutable SHAs are required")
    require(args.baseline_run > 0 and args.candidate_run > 0 and args.baseline_run != args.candidate_run, "Distinct matrix run IDs are required")
    require(not args.resume or args.apply, "--resume requires --apply")
    args.repository = args.repository.resolve()
    print(json.dumps(execute(args), indent=2))


if __name__ == "__main__":
    try:
        main()
    except (Rejected, ValueError, OSError, KeyError, TypeError) as error:
        print(f"bugfix-integration: {error}", file=sys.stderr)
        sys.exit(1)
