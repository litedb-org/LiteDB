#!/usr/bin/env python3
"""Normalize, classify, and group fuzz failures for CI triage."""

import argparse
import json
import os
import re
from collections import defaultdict
from pathlib import Path


DEFAULT_REGISTRY = (
    Path(__file__).resolve().parents[1] / "LiteDB.Fuzz" / "Corpus" / "known-findings.json"
)
VALID_STATUSES = {"known", "expected", "fixed"}


def normalize_fingerprint(value: str | None) -> str:
    if not value or not value.strip():
        return "UNKNOWN_FUZZ_FAILURE"
    normalized = value.upper()
    normalized = re.sub(
        r"\b[0-9A-F]{8}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{12}\b",
        "_VALUE_", normalized)
    normalized = re.sub(
        r"(?<![0-9A-F])[0-9A-F]{2,8}:[0-9A-F]{2,8}(?![0-9A-F])",
        "_PAGE_", normalized)
    normalized = re.sub(
        r"(?<![A-Z0-9])(SEED|STEP|PAGE|ADDRESS|ORDINAL|DOCUMENT_ID|DOCUMENTID|DOC_ID|DOCID|COUNT|RANDOM)"
        r"\s*[:=#_-]?\s*(?:0X)?[0-9A-F]+\b",
        lambda match: f"_{match.group(1)}_VALUE_", normalized)
    normalized = re.sub(r"\b0X[0-9A-F]+\b", "_VALUE_", normalized)
    normalized = re.sub(r"[^A-Z0-9]+", "_", normalized)
    return re.sub(r"_+", "_", normalized).strip("_")


def load_registry(path: Path) -> dict[tuple[str, str], dict]:
    document = json.loads(path.read_text(encoding="utf-8"))
    if document.get("schemaVersion") != 1:
        raise ValueError(f"Unsupported fuzz finding registry schema in {path}")
    registry = {}
    for finding in document.get("findings", []):
        status = finding.get("status")
        if status not in VALID_STATUSES:
            raise ValueError(f"Unknown fuzz finding status {status!r} in {path}")
        key = (finding["target"].lower(), normalize_fingerprint(finding["fingerprint"]))
        if key in registry:
            raise ValueError(f"Duplicate fuzz finding {key!r} in {path}")
        registry[key] = finding
    return registry


def triage_action(status: str, canonical_state: str | None = None) -> str:
    """Return policy action; issue state never overrides the versioned registry."""
    del canonical_state
    return {
        "known": "record",
        "expected": "suppress",
        "fixed": "regression",
        "unknown": "file",
    }[status]


def summarize(root: Path, registry: dict[tuple[str, str], dict]) -> dict:
    groups = defaultdict(lambda: {"failureIds": set(), "seeds": set(), "runs": []})
    paths = root.rglob("run.json") if root.exists() else []
    for path in paths:
        try:
            run = json.loads(path.read_text(encoding="utf-8"))
            failure_id = _failure_id(path, run)
        except (OSError, ValueError, KeyError, json.JSONDecodeError):
            continue
        if failure_id is None:
            continue
        target = str(run.get("target", "unknown"))
        fingerprint = normalize_fingerprint(failure_id)
        group = groups[(target.lower(), fingerprint)]
        group["target"] = target
        group["fingerprint"] = fingerprint
        group["failureIds"].add(failure_id)
        group["seeds"].add(int(run.get("seed", 0)))
        group["runs"].append(str(path.parent.relative_to(root)))

    failures = []
    for key, group in sorted(groups.items()):
        finding = registry.get(key)
        status = finding["status"] if finding else "unknown"
        failures.append({
            "target": group["target"],
            "fingerprint": group["fingerprint"],
            "failureIds": sorted(group["failureIds"]),
            "count": len(group["runs"]),
            "seeds": sorted(group["seeds"]),
            "runs": sorted(group["runs"]),
            "status": status,
            "canonicalIssue": finding.get("issue") if finding else None,
            "action": triage_action(status),
            "blocking": status in {"fixed", "unknown"},
        })
    return {
        "schemaVersion": 2,
        "blockingFailures": sum(1 for item in failures if item["blocking"]),
        "failures": failures,
    }


def _failure_id(path: Path, run: dict) -> str | None:
    if run.get("status") != "passed":
        return run.get("failureId") or "UNKNOWN_FUZZ_FAILURE"
    determinism = path.with_name("determinism-failure.json")
    if not determinism.exists():
        return None
    return json.loads(determinism.read_text(encoding="utf-8"))["failureId"]


def write_github_summary(document: dict) -> None:
    summary_path = os.environ.get("GITHUB_STEP_SUMMARY")
    if not summary_path:
        return
    lines = ["## Fuzz finding triage", ""]
    failures = document["failures"]
    if not failures:
        lines.append("No fuzz failures were recorded.")
    else:
        lines.extend([
            "| Fingerprint | Target | Status | Count | Canonical issue | Seeds |",
            "|---|---|---|---:|---|---|",
        ])
        for item in failures:
            issue = f"#{item['canonicalIssue']}" if item["canonicalIssue"] else "new"
            lines.append(
                f"| `{item['fingerprint']}` | {item['target']} | {item['status']} | "
                f"{item['count']} | {issue} | {', '.join(map(str, item['seeds']))} |"
            )
    with open(summary_path, "a", encoding="utf-8") as summary_file:
        summary_file.write("\n".join(lines) + "\n")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("root", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--registry", type=Path, default=DEFAULT_REGISTRY)
    parser.add_argument("--github-summary", action="store_true")
    args = parser.parse_args()

    document = summarize(args.root, load_registry(args.registry))
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(document, indent=2) + "\n", encoding="utf-8")
    if args.github_summary:
        write_github_summary(document)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
