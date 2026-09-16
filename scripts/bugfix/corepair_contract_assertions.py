"""Test-only approved #1002 contract amendment for historical preservation checks."""

import hashlib
import json
from pathlib import Path

EVIDENCE = json.loads((Path(__file__).parent / "fixtures/issue-1002-2811-corepair.json")
                      .read_text(encoding="utf-8"))
PREVIOUS_APPROVED_SHA256 = "58ab3eab73e61f69a223668158261864cdab2cc6d60dbc0df27ac03b6651fd82"

EXPANDED = json.loads((Path(__file__).parent / "fixtures/issue-1002-2811-2590-corepair.json")
                      .read_text(encoding="utf-8"))
APPROVED_SHA256 = "b4dc8542010a307c2eee3053af4cbd0dba98ee5818041c9024889f8519fdba94"


def expected_after_auto_id_corepair(number, original):
    if str(number) != "1002":
        return original
    assert original in (EVIDENCE["superseded_contract"], EVIDENCE["contract"]), "Original #1002 contract changed"
    previous = json.dumps(EVIDENCE["contract"], sort_keys=True, separators=(",", ":"), ensure_ascii=False)
    assert hashlib.sha256(previous.encode()).hexdigest() == PREVIOUS_APPROVED_SHA256
    assert EXPANDED["superseded_contract"] == EVIDENCE["contract"]
    revised = EXPANDED["contract"]
    canonical = json.dumps(revised, sort_keys=True, separators=(",", ":"), ensure_ascii=False)
    assert hashlib.sha256(canonical.encode()).hexdigest() == APPROVED_SHA256
    return revised
