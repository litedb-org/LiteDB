"""Read-only queue decisions using the controller's accepted contracts and identities."""

import base64
import json
from urllib.parse import quote

from integrate_storage import IntegrationStore, LEDGER_NAME, LOCK_NAME
from passing import passing_cases
from state import require
from storage import github


def pinned_manifest(repo, sha, ref):
    branch = github(repo, f"git/ref/heads/{quote(ref, safe='/')}")
    require(branch["object"]["sha"] == sha, "Pinned workflow branch moved")
    content = github(repo, f"contents/scripts/bugfix/issues.json?ref={sha}")
    require(content.get("type") == "file" and content.get("encoding") == "base64", "Missing trusted issue manifest")
    require(content.get("size", 0) <= 8 * 1024 * 1024, "Issue manifest exceeds the supported bound")
    return json.loads(base64.b64decode(content["content"]))


def ancestor(repo, candidate, base):
    comparison = github(repo, f"compare/{candidate}...{base}")
    return comparison.get("status") in ("identical", "ahead") and comparison.get("merge_base_commit", {}).get("sha") == candidate


def inspect_queue_issue(args, issue, contracts):
    """Take one immutable data snapshot; no write, dispatch, checkout or fetch."""
    store = IntegrationStore(args.repo)
    ledger, data_sha = store.read(LEDGER_NAME)
    require(data_sha is not None, "Controller data branch must exist before starting a queue")
    ledger = ledger or {"schema_version": 1, "issues": {}}
    head = github(args.repo, "git/ref/heads/integration/bugfixes")["object"]["sha"]
    passing_cases(ledger, head, args.test_source_sha, lambda candidate, base: ancestor(args.repo, candidate, base))
    campaign = f"{args.campaign_prefix}-{issue}"
    state = store.read_at(campaign, data_sha)
    lock = store.read_at(LOCK_NAME, data_sha)
    entry = ledger["issues"].get(str(issue))
    if entry:
        expected_tests = sorted(case["name"] for case in contracts[str(issue)]["regressions"] + contracts[str(issue)]["controls"])
        require(sorted(entry["tests"]) == expected_tests, "Accepted test identities differ from the pinned issue contract")
        accepted = store.read_at(entry["campaign"], data_sha)
        require(accepted is not None and accepted.get("phase") == "integrated" and not accepted.get("paused"),
                "Accepted ledger lacks a completed integrated campaign")
        require(accepted.get("issue") == issue and accepted.get("candidate_sha") == entry["candidate_sha"]
                and accepted.get("test_source_sha") == args.test_source_sha and accepted.get("accepted_tests") == entry,
                "Accepted ledger and integrated campaign disagree")
        return {"issue": issue, "campaign": entry["campaign"], "action": "skip_accepted", "base_sha": head,
                "candidate_sha": entry["candidate_sha"], "data_sha": data_sha}
    if state:
        for field, value in (("campaign", campaign), ("issue", issue), ("workflow_sha", args.workflow_sha),
                             ("test_source_sha", args.test_source_sha)):
            require(state.get(field) == value, f"Existing queue campaign identity changed: {field}")
        require(state.get("orchestration", {}).get("workflow_ref") == args.workflow_ref, "Existing campaign workflow ref changed")
        require(not state.get("paused") and state.get("phase") not in ("blocked", "integrated"),
                f"Queue stops at {campaign}: phase={state.get('phase')}, paused={state.get('paused')}")
        require(state.get("phase") in ("baseline", "repairing", "broad", "reviewing", "ready")
                and state.get("protocol") == "compressed-v1", "Queue cannot resume an unknown or legacy campaign protocol")
        require("check_definition" not in state, "Queue cannot resume an operator-managed revalidation")
        base = state["base_sha"]
    else:
        base = head
    resume_integration = bool(lock and lock.get("active"))
    if resume_integration:
        require(state and state.get("phase") == "ready" and lock.get("campaign") == campaign,
                "Another campaign owns the integration lock")
        require(lock.get("identity", {}).get("candidate_sha") == state["candidate_sha"]
                and lock["identity"].get("base_sha") == base, "Prepared integration identity changed")
    require(head == base or (resume_integration and lock.get("phase") == "prepared" and head == state["candidate_sha"]),
            "Integration base moved; queue will not silently rebase an existing campaign")
    return {"issue": issue, "campaign": campaign, "action": "resume" if state else "start", "base_sha": base,
            "data_sha": data_sha, "phase": state["phase"] if state else "baseline",
            "candidate_sha": state["candidate_sha"] if state else None, "resume_integration": resume_integration}
