"""Audit and repair only a proven CP1252 decoding error in accepted test names."""

import argparse
import copy
import hashlib
import json
from pathlib import Path
import sys

from integrate import integration_head
from integrate_evidence import acceptance_evidence, evidence_manifest
from integrate_storage import IntegrationStore, LEDGER_NAME, LOCK_NAME, encoded
from patching import git
from state import require


def corrected_records(state, ledger, contract):
    require(state["phase"] == "integrated", "Only integrated metadata can be repaired")
    entry = ledger["issues"][str(state["issue"])]
    require(entry == state["accepted_tests"], "Campaign and ledger already disagree")
    require(entry["candidate_sha"] == state["candidate_sha"] and entry["campaign"] == state["campaign"],
            "Accepted candidate identity differs")
    require(contract["inventory_issue"] == state["issue"]
            and contract["frozen_test_revision"] == state["test_source_sha"], "Contract identity differs")
    expected = sorted(case["name"] for case in contract["regressions"] + contract["controls"])
    corrupted = sorted(name.encode("utf-8").decode("cp1252") for name in expected)
    require(expected != corrupted and entry["tests"] == corrupted,
            "Names do not exactly match the proven UTF-8-as-CP1252 corruption")
    fixed_state, fixed_ledger = copy.deepcopy(state), copy.deepcopy(ledger)
    fixed_state["accepted_tests"]["tests"] = expected
    fixed_ledger["issues"][str(state["issue"])]["tests"] = expected
    return fixed_state, fixed_ledger, {"before": entry["tests"], "after": expected}


def execute(args):
    store = IntegrationStore(args.repo)
    state, sha = store.read(args.campaign)
    require(sha == args.expected_state_sha, "Controller state moved; inspect before repairing")
    require(state is not None, "Campaign missing")
    lock = store.read_at(LOCK_NAME, sha)
    require(lock is not None and lock.get("active") is False, "Integration is active")
    require(integration_head(args.repository, args.repo) == state["candidate_sha"],
            "Repair requires the unchanged integrated candidate")
    ledger = store.read_at(LEDGER_NAME, sha)
    git(args.repository, "fetch", "--quiet", f"https://github.com/{args.repo}.git", state["workflow_sha"], sha)
    raw_manifest = git(args.repository, "show", f"{state['workflow_sha']}:scripts/bugfix/issues.json")
    contract = json.loads(raw_manifest)["issues"][str(state["issue"])]
    updated, corrected, names = corrected_records(state, ledger, contract)
    original_path = f"evidence/{args.campaign}/test-contract.json"
    original = git(args.repository, "show", f"{sha}:{original_path}")
    old_contract = json.loads(raw_manifest.encode("utf-8").decode("cp1252"))["issues"][str(state["issue"])]
    require(json.loads(original) == old_contract, "Original contract differs beyond the known decoding error")
    # Re-download the original checks and reviews; metadata correction cannot create approval.
    verification_state = copy.deepcopy(state)
    verification_state.update(phase="ready", paused=False)
    evidence = acceptance_evidence(args.repo, verification_state)
    audit = {"schema_version": 1, "kind": "accepted-test-name-encoding-correction",
             "campaign": args.campaign, "issue": state["issue"], "prior_state_sha": sha,
             "candidate_sha": state["candidate_sha"], "workflow_sha": state["workflow_sha"],
             "original_contract_path": original_path,
             "original_contract_blob": git(args.repository, "rev-parse", f"{sha}:{original_path}"),
             "source_manifest_blob": git(args.repository, "rev-parse", f"{state['workflow_sha']}:scripts/bugfix/issues.json"),
             "names": names, "reverified_evidence": evidence_manifest(evidence)}
    prefix = f"evidence/{args.campaign}/encoding-correction-{sha[:12]}"
    updated.setdefault("metadata_corrections", []).append(prefix + "/audit.json")
    files = {LEDGER_NAME + ".json": encoded(corrected), args.campaign + ".json": encoded(updated),
             prefix + "/audit.json": encoded(audit), prefix + "/test-contract.json": encoded(contract)}
    preview = {"verified": True, "applies": args.apply, "candidate_sha": state["candidate_sha"],
               "changed_names": sum(before != after for before, after in zip(names["before"], names["after"])),
               "corrected_case_count": len(names["after"]), "audit_path": prefix + "/audit.json",
               "audit_sha256": hashlib.sha256(encoded(audit)).hexdigest()}
    if args.apply:
        require(integration_head(args.repository, args.repo) == state["candidate_sha"],
                "Integration head changed during evidence verification")
        preview["state_commit"] = store.commit(sha, files,
            f"Repair UTF-8 accepted test names for {args.campaign}\n\n"
            "Restore exact immutable contract identities after verified Windows CP1252 decoding corruption. "
            "Preserve original evidence and append a source-bound correction audit; the candidate and approvals are unchanged.")
        require(store.read_at(LEDGER_NAME, preview["state_commit"]) == corrected,
                "Corrected ledger readback differs")
        require(integration_head(args.repository, args.repo) == state["candidate_sha"],
                "Integration head changed after metadata repair; inspect before continuing")
    return preview


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", required=True)
    parser.add_argument("--campaign", required=True)
    parser.add_argument("--expected-state-sha", required=True)
    parser.add_argument("--repository", type=Path, default=Path.cwd())
    parser.add_argument("--apply", action="store_true")
    args = parser.parse_args()
    print(json.dumps(execute(args), indent=2))


if __name__ == "__main__":
    main()
