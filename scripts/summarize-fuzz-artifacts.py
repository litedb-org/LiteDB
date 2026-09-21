#!/usr/bin/env python3
"""Group fuzz failures by stable identity for CI summaries and issue triage."""

import argparse
import json
import os
from collections import defaultdict
from pathlib import Path


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("root", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--github-summary", action="store_true")
    args = parser.parse_args()

    groups = defaultdict(lambda: {"targets": set(), "seeds": set(), "runs": []})
    for path in args.root.rglob("run.json") if args.root.exists() else []:
        try:
            run = json.loads(path.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError):
            continue
        if run.get("status") == "passed":
            determinism = path.with_name("determinism-failure.json")
            if not determinism.exists():
                continue
            failure_id = json.loads(determinism.read_text(encoding="utf-8"))["failureId"]
        else:
            failure_id = run.get("failureId") or "UNKNOWN_FUZZ_FAILURE"
        group = groups[failure_id]
        group["targets"].add(str(run.get("target", "unknown")))
        group["seeds"].add(int(run.get("seed", 0)))
        group["runs"].append(str(path.parent.relative_to(args.root)))

    failures = []
    for failure_id, group in sorted(groups.items()):
        failures.append({
            "failureId": failure_id,
            "count": len(group["runs"]),
            "targets": sorted(group["targets"]),
            "seeds": sorted(group["seeds"]),
            "runs": sorted(group["runs"]),
        })
    document = {"schemaVersion": 1, "failures": failures}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(document, indent=2) + "\n", encoding="utf-8")

    if args.github_summary and os.environ.get("GITHUB_STEP_SUMMARY"):
        lines = ["## Fuzz failure groups", ""]
        if not failures:
            lines.append("No fuzz failures were recorded.")
        else:
            lines.extend(["| Failure ID | Count | Targets | Seeds |", "|---|---:|---|---|"])
            for item in failures:
                lines.append(
                    f"| `{item['failureId']}` | {item['count']} | "
                    f"{', '.join(item['targets'])} | {', '.join(map(str, item['seeds']))} |"
                )
        with open(os.environ["GITHUB_STEP_SUMMARY"], "a", encoding="utf-8") as summary:
            summary.write("\n".join(lines) + "\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
