"""Publish one informational sweep heartbeat; this never authorizes acceptance."""

import argparse
from collections import Counter
from datetime import datetime, timezone
import json
from urllib.parse import quote

from state import NAME, require
from storage import Store, run

ISSUE = 2890
BOT = "github-actions[bot]"


def marker(sweep):
    require(NAME.fullmatch(sweep), "Invalid sweep name")
    return f"<!-- litedb-bugfix-sweep-status:{sweep}:v1 -->"


def render(repo, sweep, manifest, state_sha, run_id, campaigns):
    """Report journal dispositions separately from final full-matrix acceptance."""
    now = datetime.now(timezone.utc).strftime("%Y-%m-%d %H:%M:%S UTC")
    base = f"https://github.com/{repo}"
    lines = [marker(sweep), f"## Hosted sweep: `{sweep}`", "",
             f"Last observation: **{now}** · [Scheduler run {run_id}]({base}/actions/runs/{run_id})", ""]
    if manifest is None:
        lines += ["The scheduler has not initialized this sweep's durable manifest yet.",
                  "Check the linked run for setup errors. No completion is claimed."]
        return "\n".join(lines) + "\n"
    require(manifest.get("kind") == "hosted-bugfix-sweep" and manifest.get("sweep") == sweep,
            "Unexpected sweep manifest")
    spec = manifest["specification"]
    counts = Counter(item["status"] for item in manifest["issues"].values())
    phase = "paused" if manifest.get("paused") else manifest.get("phase", "unknown")
    lines += [f"Journal phase: **{phase}**. "
              f"Accepted: **{counts['accepted']}**; active: **{counts['active']}**; "
              f"pending: **{counts['pending']}**; deferred: **{counts['deferred']}**.", "",
              f"[Exact state snapshot]({base}/blob/{state_sha}/sweep-{quote(sweep, safe='')}.json) · "
              f"[Runtime]({base}/tree/{spec['workflow_sha']}) · "
              f"[Integration branch]({base}/tree/integration/bugfixes)", "",
              "These counts describe this approved queue. They do not mean the complete bug inventory "
              "or final full-matrix validation is finished.", ""]
    cooldown = manifest.get("cooldown_until", 0)
    if cooldown:
        until = datetime.fromtimestamp(cooldown, timezone.utc).strftime("%Y-%m-%d %H:%M:%S UTC")
        lines += [f"Recorded cooldown deadline: **{until}**. See the state snapshot for its disposition.", ""]
    lines += ["| Issue | Queue disposition | Campaign evidence |", "| --- | --- | --- |"]
    for issue in spec["issues"]:
        item = manifest["issues"][str(issue)]
        name, campaign = campaigns.get(str(issue), (None, None))
        evidence = "Not started"
        if name:
            label = campaign.get("phase", "unknown") if campaign else "journal"
            evidence = f"[{label}]({base}/blob/{state_sha}/{quote(name, safe='')}.json)"
        lines.append(f"| #{issue} | {item['status']} | {evidence} |")
    requests = []
    for name, campaign in campaigns.values():
        if campaign:
            for request in campaign.get("orchestration", {}).get("requests", {}).values():
                if type(request.get("run_id")) is int:
                    requests.append((request.get("started_at", 0), name, request["run_id"]))
    if requests:
        lines += ["", "### Recent workflow runs", ""]
        for _, name, worker_run in sorted(requests, reverse=True)[:8]:
            lines.append(f"- `{name}`: [run {worker_run}]({base}/actions/runs/{worker_run})")
    lines += ["", "Deferred candidates remain unaccepted. Their evidence and review obligations are retained.",
              "The scheduled workflow can wake without this chat or a local controller. "
              "A successful scheduler run alone does not establish that any fix passed review."]
    return "\n".join(lines) + "\n"


def publish(repo, sweep, body):
    pages = json.loads(run(["gh", "api", "--paginate", "--slurp",
                           f"repos/{repo}/issues/{ISSUE}/comments?per_page=100"]))
    existing = [comment for page in pages for comment in page
                if comment.get("user", {}).get("login") == BOT
                and comment.get("user", {}).get("type") == "Bot"
                and comment.get("body", "").startswith(marker(sweep) + "\n")]
    require(len(existing) <= 1, "More than one bot heartbeat exists for this sweep")
    endpoint = (f"repos/{repo}/issues/comments/{existing[0]['id']}" if existing
                else f"repos/{repo}/issues/{ISSUE}/comments")
    response = json.loads(run(["gh", "api", "--method", "PATCH" if existing else "POST",
                               endpoint, "--input", "-"], input_text=json.dumps({"body": body})))
    require(response.get("body") == body, "Heartbeat readback differs from submitted state")
    return response["html_url"]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", required=True)
    parser.add_argument("--sweep", required=True)
    parser.add_argument("--run-id", required=True, type=int)
    parser.add_argument("--issue", type=int, default=ISSUE)
    args = parser.parse_args()
    require(args.repo == "litedb-org/LiteDB" and args.issue == ISSUE,
            "This publisher is restricted to the approved tracking issue")
    require(args.run_id > 0, "Positive hosted run ID required")
    marker(args.sweep)
    store = Store(args.repo)
    manifest, state_sha = store.read("sweep-" + args.sweep)
    campaigns = {}
    if manifest:
        # Completed/deferred entries already carry their campaign identity.
        # Only the active campaign needs another read; avoid dozens of API calls
        # on every scheduled heartbeat as the accepted queue grows.
        from integrate_storage import IntegrationStore
        snapshot = IntegrationStore(args.repo)
        for issue, item in manifest["issues"].items():
            investigation = item.get("investigation", {})
            decision = item.get("decision", {})
            name = investigation.get("campaign") or decision.get("campaign")
            if not name:
                continue
            require(NAME.fullmatch(name), "Invalid campaign reference")
            if item["status"] == "active":
                campaign = snapshot.read_at(name, state_sha)
            elif investigation:
                campaign = {"phase": investigation["phase"]}
            elif item["status"] == "accepted":
                campaign = {"phase": "integrated"}
            else:
                campaign = None
            if campaign:
                campaigns[issue] = (name, campaign)
    print(publish(args.repo, args.sweep,
                  render(args.repo, args.sweep, manifest, state_sha, args.run_id, campaigns)))


if __name__ == "__main__":
    main()
