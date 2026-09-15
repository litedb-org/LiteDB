"""Small CLI for trusted workflow jobs or an authenticated operator."""

import argparse
import json
from pathlib import Path
import sys

from artifacts import HASH_FIELDS
from passing import load_snapshot
from state import NAME, Rejected, apply_event, new_state, require
from storage import Store, verify_run


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", required=True, help="GitHub owner/repository")
    parser.add_argument("--campaign", required=True)
    commands = parser.add_subparsers(dest="command", required=True)
    initialize = commands.add_parser("init")
    initialize.add_argument("--issue", type=int, required=True)
    initialize.add_argument("--base-sha", required=True)
    initialize.add_argument("--test-source-sha", required=True)
    initialize.add_argument("--workflow-sha", required=True)
    initialize.add_argument("--repository", type=Path, default=Path.cwd())
    commands.add_parser("get")
    record = commands.add_parser("record")
    record.add_argument("--event-json", type=Path, required=True)
    record.add_argument("--allow-workflow", action="append", default=[],
                        help="Trusted exact workflow path; repeat for check/validator workflows")
    args = parser.parse_args()
    require(NAME.fullmatch(args.campaign), "Invalid campaign name")
    store = Store(args.repo)
    state, expected_sha = store.read(args.campaign)
    if args.command == "init":
        require(state is None, "Campaign already exists")
        state = new_state(args.campaign, args.issue, args.base_sha, args.test_source_sha, args.workflow_sha)
        state["passing_contract"], _ = load_snapshot(args.repository, args.repo, expected_sha,
                                                     args.base_sha, args.test_source_sha)
        expected_sha = store.write(state, expected_sha)
    else:
        require(state is not None, "Campaign does not exist")
        if args.command == "record":
            event = json.loads(args.event_json.read_text(encoding="utf-8-sig"))
            require(not any(field in event for field in HASH_FIELDS), "Report hashes are controller-owned")
            previous = next((item for item in state["history"] if item["event_id"] == event.get("event_id")), None)
            if previous:
                supplied = {key: value for key, value in previous.items() if key not in HASH_FIELDS}
                require(supplied == event, "Event ID reused with different content")
                event = previous
            updated = apply_event(state, event)
            if updated != state:
                if event["kind"] in ("baseline", "focused", "broad", "review", "acceptance"):
                    event.update(verify_run(args.repo, event, args.allow_workflow))
                    updated = apply_event(state, event)
                expected_sha = store.write(updated, expected_sha)
            state = updated
    print(json.dumps({"state_commit": expected_sha, "state": state}, indent=2))


if __name__ == "__main__":
    try:
        main()
    except (Rejected, OSError, KeyError, TypeError, json.JSONDecodeError) as error:
        print(f"bugfix-controller: {error}", file=sys.stderr)
        sys.exit(1)
