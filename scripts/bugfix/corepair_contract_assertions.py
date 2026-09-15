"""Test-only approved #1002 contract amendment for historical preservation checks."""

import hashlib
import json
from pathlib import Path

EVIDENCE = json.loads((Path(__file__).parent / "fixtures/issue-1002-2811-corepair.json")
                      .read_text(encoding="utf-8"))
APPROVED_SHA256 = "58ab3eab73e61f69a223668158261864cdab2cc6d60dbc0df27ac03b6651fd82"


def expected_after_auto_id_corepair(number, original):
    if str(number) != "1002":
        return original
    assert original == EVIDENCE["superseded_contract"], "Original #1002 contract changed"
    revised = EVIDENCE["contract"]
    canonical = json.dumps(revised, sort_keys=True, separators=(",", ":"), ensure_ascii=False)
    assert hashlib.sha256(canonical.encode()).hexdigest() == APPROVED_SHA256
    return revised
