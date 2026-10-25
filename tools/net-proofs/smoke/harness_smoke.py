"""Harness self-check for net_proof.py (NOT a safety net; it proves nothing about a defect).

Run by the `harness-smoke` entry of .github/safety/net-proofs.json in each overlaid,
built tree. It exits 2 ("broken") unless the patch overlay, the adapter overlay and the
build all reached the tree, so a broken pipeline can never classify as proven. Then it
"fires" (exit 1, HARNESS-SMOKE-FIRED) where the #3077 guard test file is absent and
stays quiet (exit 0) where it exists.
"""
import argparse
import json
import sys
from pathlib import Path

GUARD = "LiteDB.Tests/Engine/SharedSelfCloseCallback_Tests.cs"

parser = argparse.ArgumentParser()
parser.add_argument("--tree", required=True)
parser.add_argument("--artifacts", required=True)
args = parser.parse_args()
tree = Path(args.tree)
checks = {
    "patch overlay applied": (tree / "net-proof-smoke/patched.txt").is_file(),
    "adapter overlay copied": (tree / "net-proof-smoke/marker.txt").is_file(),
    "LiteDB built": any(tree.glob("LiteDB/bin/Release/*/LiteDB.dll")),
}
broken = [name for name, ok in checks.items() if not ok]
fired = not (tree / GUARD).is_file()
Path(args.artifacts, "smoke.json").write_text(json.dumps({"checks": checks, "guardPresent": not fired}) + "\n")
if broken:
    print(f"HARNESS-SMOKE-BROKEN: {', '.join(broken)}")
    sys.exit(2)
if fired:
    print(f"HARNESS-SMOKE-FIRED: guard-test-absent ({GUARD})")
    sys.exit(1)
print(f"harness smoke quiet: {GUARD} present")
