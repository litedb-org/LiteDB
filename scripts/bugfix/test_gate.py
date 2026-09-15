from contextlib import redirect_stdout
import io
import json
from pathlib import Path
import tempfile
import unittest
import xml.etree.ElementTree as ET

from gate import main
from test_support import target_run, xml_report


class CommandTests(unittest.TestCase):
    def test_focused_cli_emits_bound_evidence(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            baseline = root / "baseline.trx"
            candidate = root / "candidate.trx"
            output = root / "report.json"
            for path, run in ((baseline, target_run()), (candidate, target_run(False))):
                ET.ElementTree(xml_report(run)).write(path, encoding="utf-8")
            arguments = ["focused", "--issue", "2874", "--base-sha", "a" * 40,
                         "--candidate-sha", "b" * 40, "--test-definition-sha", "c" * 40,
                         "--environment", "linux-x64-net8.0", "--baseline-trx", str(baseline),
                         "--baseline-exit-code", "1", "--candidate-trx", str(candidate),
                         "--candidate-exit-code", "0", "--output", str(output)]
            with redirect_stdout(io.StringIO()):
                self.assertEqual(0, main(arguments))
            report = json.loads(output.read_text(encoding="utf-8"))
            self.assertEqual("behavior_correct", report["outcome"])
            self.assertEqual("b" * 40, report["provenance"]["candidate_sha"])
            self.assertEqual(64, len(report["candidate_trx_sha256"]))
            candidate.unlink()
            with redirect_stdout(io.StringIO()):
                self.assertEqual(1, main(arguments))
            report = json.loads(output.read_text(encoding="utf-8"))
            self.assertFalse(report["accepted"])
            self.assertEqual("harness_error", report["outcome"])


if __name__ == "__main__":
    unittest.main()
