import json
import os
import tempfile
import unittest
from pathlib import Path

import render_net_proofs as render
from safety_fixtures import run_quietly

SHA = "5dd942a7367c361fadd600be4ce10aace2768b27"
LEDGER = {"schemaVersion": 1, "proofs": [
    {"id": "harness-smoke", "rows": [], "level": "harness-smoke", "independence": "harness-smoke: x",
     "net": {"name": "harness-smoke (not a net)"}},
    {"id": "row17-ownership", "rows": [17], "level": "reproduction", "independence": "designed-from-invariant",
     "net": {"name": "ownership"},
     "results": {"state": "proven", "passed": True, "knownBad": {"fired": True, "assertion": "OWNERSHIP_X_RELEASED"}}},
    {"id": "row12-pbt", "rows": [12], "level": "generic", "independence": "tuned-after-fix: rule from later docs",
     "net": {"name": "parallel-property"}, "results": {"state": "not-fired", "passed": False}},
    {"id": "row16-wait-graph", "rows": [16], "level": "generic", "independence": "designed-from-invariant",
     "net": {"name": "wait | graph"}},
]}


class RenderNetProofsTests(unittest.TestCase):
    def setUp(self):
        self.dir = Path(tempfile.mkdtemp(prefix="net-proofs-"))
        self.ledger = self.dir / "net-proofs.json"
        self.ledger.write_text(json.dumps(LEDGER))
        self.doc = self.dir / "doc.md"
        self.doc.write_text(f"# Doc\n\n{render.BEGIN}\nold table\n{render.END}\n\nAfter.\n")

    def test_rows_skip_harness_smoke_and_render_skeletons_as_not_attempted(self):
        block = render.render(LEDGER)
        lines = block.splitlines()
        self.assertEqual(lines[0], render.BEGIN)
        self.assertEqual(lines[-1], render.END)
        body = [line for line in lines if line.startswith("| 1")]
        self.assertEqual([line.split("|")[1].strip() for line in body], ["16", "17"])
        self.assertIn("| wait / graph | generic | designed | **not attempted** | - |", body[0])
        self.assertIn("| reproduction | designed | **proven** | OWNERSHIP_X_RELEASED |", body[1])
        self.assertIn("Not fired (1 entries, listed in the full matrix", block)  # never dropped silently
        self.assertIn("): parallel-property 1.", block)
        self.assertNotIn("harness-smoke", block)
        self.assertIn("1 of 3 entries proven", block)

    def test_write_replaces_only_the_block_and_check_detects_staleness(self):
        code, output = run_quietly(render.main, ["--ledger", str(self.ledger), "--check", str(self.doc)])
        self.assertEqual(code, 1, output)
        self.assertIn("stale", output)
        code, _ = run_quietly(render.main, ["--ledger", str(self.ledger), "--write", str(self.doc)])
        self.assertEqual(code, 0)
        text = self.doc.read_text()
        self.assertTrue(text.startswith("# Doc\n\n" + render.BEGIN))
        self.assertTrue(text.endswith(render.END + "\n\nAfter.\n"))
        self.assertNotIn("old table", text)
        code, output = run_quietly(render.main, ["--ledger", str(self.ledger), "--check", str(self.doc)])
        self.assertEqual(code, 0, output)

    def test_a_file_without_the_block_fails(self):
        plain = self.dir / "plain.md"
        plain.write_text("# Nothing generated here\n")
        code, output = run_quietly(render.main, ["--ledger", str(self.ledger), "--write", str(plain)])
        self.assertEqual(code, 1)
        self.assertIn("has no", output)

    def test_retrospective_blocks_summarize_rows_families_and_entries(self):
        ledger = json.loads(json.dumps(LEDGER))
        ledger["defects"] = {str(row): {"kind": "plan", "defect": f"defect {row}", "knownBad": {"commit": "a" * 40},
                                        "fix": {"commit": "b" * 40}} for row in (12, 16, 17, 18)}
        ledger["proofs"][1]["net"]["family"] = "Ownership oracle"
        ledger["proofs"][2]["results"] = {"state": "proven", "passed": True, "caveat": "present before K",
                                          "knownBad": {"fired": True, "timeToYellSeconds": 2.5, "reproduced": "3/3"}}
        ledger["proofs"].append({"id": "row16-sweep", "rows": [16], "level": "generic",
                                 "independence": "designed-from-invariant", "net": {"name": "teardown-sweep"},
                                 "results": {"state": "fired-differently", "knownBad": {"fired": True}}})
        doc = self.dir / "retro.md"
        markers = "".join(f"{render.begin(name)}\n{render.end(name)}\n\n"
                          for name in ("verification-matrix", "net-firings", "net-entries"))
        doc.write_text("# Retro\n\n" + markers)
        self.ledger.write_text(json.dumps(ledger))
        code, output = run_quietly(render.main, ["--ledger", str(self.ledger), "--write", str(doc)])
        self.assertEqual(code, 0, output)
        self.assertIn("verification-matrix, net-firings, net-entries", output)
        text = doc.read_text()
        matrix = [line for line in text.splitlines() if line.startswith("| 1")]
        self.assertTrue(matrix[0].startswith("| 12 | defect 12 | `aaaaaaaa` → `bbbbbbbb` | **generic (caveat)** |"))
        self.assertIn("| **fired differently only** | - | `row16-sweep` |", matrix[1])
        self.assertIn("| **reproduction** | `row17-ownership` (repro) |", matrix[2])
        self.assertIn("| 18 | defect 18 |", matrix[3])
        self.assertIn("| **none** |", matrix[3])
        self.assertIn("| teardown-sweep | 0 | 1: 16 | 0 | 0 | 0 | 0 |", text)
        self.assertIn("| Ownership oracle | 0 | 0 | 1: 17 | 0 | 0 | 0 |", text)
        self.assertIn("| 2.5 s | 3/3 | `row12-pbt` |", text)
        code, output = run_quietly(render.main, ["--ledger", str(self.ledger), "--check", str(doc)])
        self.assertEqual(code, 0, output)
        ledger["proofs"].pop()
        self.ledger.write_text(json.dumps(ledger))
        code, output = run_quietly(render.main, ["--ledger", str(self.ledger), "--check", str(doc)])
        self.assertEqual(code, 1, output)
        self.assertIn("stale", output)

    def test_the_repository_ledger_renders(self):
        code, output = run_quietly(render.main, [])
        self.assertEqual(code, 0, output)
        self.assertIn(render.BEGIN, output)


if __name__ == "__main__":
    unittest.main()
