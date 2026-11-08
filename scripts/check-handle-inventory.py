#!/usr/bin/env python3
"""Check completeness and destination existence of the source-port disposition."""
import json
from pathlib import Path

root = Path(__file__).resolve().parent.parent
manifest = json.loads((root / "docs/transaction-handles/source-inventory.json").read_text())
suites = {test["source"].split("#")[0] for test in manifest["tests"]}
assert len(suites) == manifest["suite_count"] == 64
assert sum(manifest["counts"].values()) == 64
assert len({test["source"] for test in manifest["tests"]}) == len(manifest["tests"])
for test in manifest["tests"]:
    assert test["disposition"] in manifest["counts"]
    assert test["invariant"] and test["destinations"]
    for destination in test["destinations"]:
        assert (root / destination).is_file(), destination
for destination in (root / "docs/transaction-handles/target-inventory.txt").read_text().splitlines():
    assert (root / destination).is_file(), destination
print(f"PASS {len(suites)} source suites, {len(manifest['tests'])} source tests and all target paths")
