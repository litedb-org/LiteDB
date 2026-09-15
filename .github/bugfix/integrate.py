"""Verify and atomically integrate one fully validated correctness fix."""

import argparse
import hashlib
import json
from pathlib import Path
import sys

from evidence import event_for
from integrate_evidence import acceptance_evidence, evidence_manifest, validate_archived_evidence
from integrate_storage import IntegrationStore, LEDGER_NAME, LOCK_NAME, encoded
from patching import git, worktree
from profiles import build_profile, observe_source_context
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
             "validation_provenance": report["provenance"], "target_jobs": report["target_jobs"],
             "coverage_gaps": report["coverage_gaps"], "architecture_limitations": report.get("architecture_limitations", [])}
    entry["final_matrix_status"] = "pending"
    entry["source_context_changes"] = report.get("source_context_changes", [])
    if "acceptance_profile" in state:
        entry["acceptance_profile"] = state["acceptance_profile"]
    if report["provenance"].get("validation_scope") != "per-fix":
        entry["matrix_provenance"] = report["provenance"]
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


def validation_report(state, definition, accepted, observations):
    return {"provenance": {"validation_scope": "per-fix", "candidate_run_id": accepted["run_id"],
            "baseline_run_id": state["evidence"]["baseline"]["run_id"], "base_sha": state["base_sha"],
            "candidate_sha": state["candidate_sha"], "test_source_sha": state["test_source_sha"],
            "worker_review_workflow_sha": state["workflow_sha"], "check_workflow_sha": definition,
            "acceptance_profile_sha256": state.get("acceptance_profile", {}).get("profile_sha256")},
            "target_jobs": [lane["artifact"] for lane in accepted.get("matrix", [])], "coverage_gaps": [],
            "source_context_changes": observations}


def add_controller_evidence(files, state, report, contract, supplemental):
    files.update(supplemental)
    files["per-fix-verdict.json"] = encoded(report)
    files["test-contract.json"] = encoded(contract)
    files["evidence-manifest.json"] = encoded(evidence_manifest(files))
    return files


def prepared_evidence(store, state, lock, state_sha, report, contract, supplemental):
    prefix = f"evidence/{state['campaign']}/"
    require(lock.get("evidence_prefix") == prefix, "Prepared evidence prefix changed")
    files = store.read_prefix_at(prefix, state_sha)
    manifest_raw = files.pop("evidence-manifest.json", None)
    require(manifest_raw is not None, "Prepared evidence manifest is missing")
    require(lock.get("evidence_manifest_sha256") == hashlib.sha256(manifest_raw).hexdigest(),
            "Prepared evidence manifest identity changed")
    manifest = json.loads(manifest_raw)
    require(manifest == evidence_manifest(files), "Prepared evidence bytes differ from their durable manifest")
    expected = {**supplemental, "per-fix-verdict.json": encoded(report), "test-contract.json": encoded(contract)}
    for name, content in expected.items():
        require(files.get(name) == content, f"Prepared controller evidence changed: {name}")
    validate_archived_evidence(files, state)
    files["evidence-manifest.json"] = manifest_raw
    return files


def execute(args):
    store = IntegrationStore(args.repo)
    state, _ = store.read(args.campaign)
    require(state is not None, "Campaign does not exist")
    require(state["candidate_sha"] == args.candidate_sha, "Explicit candidate differs from campaign state")
    if state["phase"] == "integrated":
        require(integration_head(args.repository, args.repo) == args.candidate_sha, "Integrated campaign branch has since moved")
        return {"phase": "integrated", "candidate_sha": args.candidate_sha, "already_complete": True}
    require(state["phase"] == "ready" and not state["paused"], "Only a ready, unpaused campaign may integrate")
    definition = state.get("check_definition", {}).get("workflow_sha", state["workflow_sha"])
    git(args.repository, "fetch", "--quiet", f"https://github.com/{args.repo}.git", state["base_sha"],
        state["candidate_sha"], state["workflow_sha"], definition)
    tree = tested_tree(args.repository, args.repo, state)
    stage = "broad" if state.get("protocol") == "compressed-v1" else "acceptance"
    accepted = state["evidence"][stage]
    observations = []
    supplemental = {}
    if "acceptance_profile" in state:
        with worktree(args.repository, definition) as control:
            require(build_profile(control, args.repository, state) == state["acceptance_profile"],
                    "Acceptance profile cannot be reproduced from trusted definition and exact candidate diff")
            supplemental["acceptance-profile.json"] = encoded(state["acceptance_profile"])
            supplemental["acceptance_profile.py"] = (control / ".github/bugfix/acceptance_profile.py").read_bytes()
            observations = observe_source_context(control, args.repository, state)
            require(accepted.get("source_context_changes", []) == observations, "Accepted source-context observations changed")
            if observations:
                supplemental["source_context.py"] = (control / "scripts/bugfix/source_context.py").read_bytes()
    report = validation_report(state, definition, accepted, observations)
    raw_manifest = git(args.repository, "show", f"{state['workflow_sha']}:scripts/bugfix/issues.json")
    contract = json.loads(raw_manifest)["issues"][str(state["issue"])]
    require(contract["frozen_test_revision"] == state["test_source_sha"], "Accepted contract changed regression source")
    identity = {"campaign": state["campaign"], "base_sha": state["base_sha"], "candidate_sha": state["candidate_sha"],
                "check_workflow_sha": definition, "acceptance_run": accepted["run_id"],
                "acceptance_profile_sha256": state.get("acceptance_profile", {}).get("profile_sha256")}
    lock = None
    prepared_sha = None
    if args.apply and args.resume:
        lock = store.acquire(identity, resume=True)
        lock, prepared_sha = store.require_lock(lock["token"])
    if lock is not None and lock["phase"] == "prepared":
        files = prepared_evidence(store, state, lock, prepared_sha, report, contract, supplemental)
    else:
        files = add_controller_evidence(acceptance_evidence(args.repo, state), state, report, contract, supplemental)
    if not args.apply:
        return {"phase": "verified", "candidate_sha": args.candidate_sha, "candidate_tree_sha": tree,
                "evidence_files": len(files), "coverage_gaps": report["coverage_gaps"], "applies": False}
    if lock is None:
        lock = store.acquire(identity, resume=False)
    current_head = integration_head(args.repository, args.repo)
    require(current_head == state["base_sha"] or (args.resume and lock["phase"] == "prepared"
            and current_head == state["candidate_sha"]), "Integration branch moved before evidence preparation")
    lock, sha = store.require_lock(lock["token"])
    require(store.read_at(args.campaign, sha) == state, "Campaign changed while integration evidence was validated")
    if lock["phase"] == "acquired":
        lock.update(phase="prepared", candidate_tree_sha=tree, evidence_prefix=f"evidence/{args.campaign}/",
                    evidence_manifest_sha256=hashlib.sha256(files["evidence-manifest.json"]).hexdigest())
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
    parser.add_argument("--repository", type=Path, default=Path.cwd())
    parser.add_argument("--apply", action="store_true", help="Advance integration after all evidence passes; otherwise verify only")
    parser.add_argument("--resume", action="store_true", help="Resume this exact transaction's existing persistent lock")
    args = parser.parse_args(argv)
    require(NAME.fullmatch(args.campaign), "Invalid campaign name")
    require(SHA.fullmatch(args.candidate_sha), "Full immutable candidate SHA is required")
    require(not args.resume or args.apply, "--resume requires --apply")
    args.repository = args.repository.resolve()
    print(json.dumps(execute(args), indent=2))


if __name__ == "__main__":
    try:
        main()
    except (Rejected, ValueError, OSError, KeyError, TypeError) as error:
        print(f"bugfix-integration: {error}", file=sys.stderr)
        sys.exit(1)
