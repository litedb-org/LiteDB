#!/usr/bin/env python3
"""Regression tests for fuzz finding normalization and registry policy."""

import importlib.util
import json
import sys
import tempfile
import unittest
from pathlib import Path


SCRIPT = Path(__file__).with_name("summarize-fuzz-artifacts.py")
sys.dont_write_bytecode = True
SPEC = importlib.util.spec_from_file_location("fuzz_triage", SCRIPT)
TRIAGE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(TRIAGE)


class FuzzFindingTriageTests(unittest.TestCase):
    def test_open_and_closed_known_findings_stay_canonical(self):
        self.assertEqual("record", TRIAGE.triage_action("known", "open"))
        self.assertEqual("record", TRIAGE.triage_action("known", "closed"))

    def test_expected_fixed_and_unknown_have_distinct_actions(self):
        self.assertEqual("suppress", TRIAGE.triage_action("expected", "open"))
        self.assertEqual("regression", TRIAGE.triage_action("fixed", "closed"))
        self.assertEqual("file", TRIAGE.triage_action("unknown", None))

    def test_fingerprint_removes_volatile_values_but_keeps_defect_identity(self):
        first = "orphan seed=17 step 44 page 0006:21 doc_id=81 count=3"
        second = "orphan seed=99 step 101 page 0004:08 doc_id=92 count=7"
        different = "backlink seed=17 step 44 page 0006:21 doc_id=81 count=3"

        self.assertEqual(TRIAGE.normalize_fingerprint(first), TRIAGE.normalize_fingerprint(second))
        self.assertNotEqual(TRIAGE.normalize_fingerprint(first), TRIAGE.normalize_fingerprint(different))

    def test_summary_classifies_registry_states_and_unknown(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            registry_path = root / "registry.json"
            registry_path.write_text(json.dumps({
                "schemaVersion": 1,
                "findings": [
                    {"target": "vector", "fingerprint": "ORPHAN_PAGE_0006:21", "issue": 1, "status": "known"},
                    {"target": "rebuild", "fingerprint": "CHECKSUM_COUNT=7", "issue": 2, "status": "expected"},
                    {"target": "api", "fingerprint": "HANG_API", "issue": 3, "status": "fixed"},
                ],
            }), encoding="utf-8")
            self._write_run(root, "known", "vector", "ORPHAN_PAGE_0004:08", 11)
            self._write_run(root, "expected", "rebuild", "CHECKSUM_COUNT=99", 12)
            self._write_run(root, "fixed", "api", "HANG_API", 13)
            self._write_run(root, "unknown", "value", "NEW_DEFECT", 14)

            document = TRIAGE.summarize(root, TRIAGE.load_registry(registry_path))

        by_target = {item["target"]: item for item in document["failures"]}
        self.assertEqual("known", by_target["vector"]["status"])
        self.assertEqual("expected", by_target["rebuild"]["status"])
        self.assertEqual("fixed", by_target["api"]["status"])
        self.assertEqual("unknown", by_target["value"]["status"])
        self.assertEqual(2, document["blockingFailures"])

    @staticmethod
    def _write_run(root: Path, name: str, target: str, failure_id: str, seed: int) -> None:
        directory = root / name
        directory.mkdir()
        (directory / "run.json").write_text(json.dumps({
            "status": "failed", "target": target, "failureId": failure_id, "seed": seed,
        }), encoding="utf-8")


if __name__ == "__main__":
    unittest.main()
