import json
import os
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

import compose_repro_matrix


ROOT = Path(__file__).resolve().parents[2]


class ReproMatrixTests(unittest.TestCase):
    def compose(self, repros, tier="full", include_retired=False, retired=None, invalid=None):
        with tempfile.TemporaryDirectory() as temporary:
            workspace = Path(temporary)
            (workspace / ".github").mkdir()
            for name in ("os-matrix.json", "repro-ci.json"):
                (workspace / ".github" / name).write_bytes((ROOT / ".github" / name).read_bytes())
            policy = json.loads((workspace / ".github/repro-ci.json").read_text()) if retired is None else retired
            (workspace / ".github/repro-ci.json").write_text(json.dumps(policy))
            for entry in policy.values():
                for reference in entry.get("tests", []):
                    path = reference.split("#")[0]
                    if (ROOT / path).exists():
                        target = workspace / path
                        target.parent.mkdir(parents=True, exist_ok=True)
                        target.write_bytes((ROOT / path).read_bytes())
            (workspace / "repros.json").write_text(json.dumps({"repros": repros, "invalid": invalid}))
            output = workspace / "output"
            summary = workspace / "summary"
            with patch.dict(os.environ, {
                "GITHUB_WORKSPACE": str(workspace), "GITHUB_OUTPUT": str(output),
                "GITHUB_STEP_SUMMARY": str(summary), "CI_TIER": tier,
                "INCLUDE_REGRESSION_REPROS": str(include_retired).lower(),
            }):
                compose_repro_matrix.main()
            values = dict(line.split("=", 1) for line in output.read_text().splitlines())
            return json.loads(values["matrix"])["include"], json.loads(values["skipped"]), summary.read_text()

    def inventory(self):
        return [dict(name=m["id"], supports=m.get("supports", ["any"]))
                for path in (ROOT / "LiteDB.ReproRunner/Repros").glob("*/repro.json")
                for m in [json.loads(path.read_text())]]

    def test_fixed_repros_are_replaced_by_real_test_methods_in_both_tiers(self):
        for tier, count in [("pr", 2), ("full", 3)]:
            with self.subTest(tier=tier):
                entries, skipped, _ = self.compose(self.inventory(), tier)
                self.assertEqual(count, len(entries))
                self.assertEqual({"Issue_2561_TransactionMonitor"}, {e["repro"] for e in entries})
                self.assertEqual(3, len(skipped))

    def test_manual_override_keeps_historical_reproductions_available(self):
        entries, skipped, _ = self.compose(self.inventory(), include_retired=True)
        # 2561 and 2586 on all three images, 2614 and the Linux-only Issue_3027_LegacyDroppedIndex on two.
        self.assertEqual(10, len(entries))
        self.assertEqual([], skipped)

    def test_new_repros_are_not_silently_retired(self):
        entries, _, _ = self.compose([{"name": "NewSafetyCheck"}])
        self.assertEqual(3, len(entries))

    def test_missing_replacement_test_blocks_retirement(self):
        with self.assertRaisesRegex((ValueError, FileNotFoundError), "missing"):
            self.compose([], retired={"Fixed": {"reason": "covered", "tests": ["LiteDB.Tests/missing.cs#Missing"]}})

    def test_retirement_requires_reason_and_tests(self):
        with self.assertRaisesRegex(ValueError, "needs a reason and replacement tests"):
            self.compose([], retired={"Fixed": {"tests": []}})

    def test_malformed_and_substring_references_cannot_retire_a_repro(self):
        source = "LiteDB.Tests/Issues/Issue2614_InitializationCleanup_Tests.cs"
        for reference in [source, source + "#", source + "#ReleasesFileHandle_AndAllowsRetry"]:
            with self.subTest(reference=reference):
                with self.assertRaisesRegex(ValueError, "Missing replacement test for Fixed:"):
                    self.compose([], retired={"Fixed": {"reason": "covered", "tests": [reference]}})

    def test_invalid_manifests_and_tiers_fail_closed(self):
        with self.assertRaisesRegex(ValueError, "Invalid repro manifests"):
            self.compose([], invalid=[{"name": "broken", "errors": ["bad manifest"]}])
        with self.assertRaisesRegex(ValueError, "Unsupported CI tier"):
            self.compose([], tier="typo")

    def test_pr_selection_respects_pinned_and_excluded_runner_labels(self):
        for constraints in [{"includeLabels": ["ubuntu-24.04"]}, {"excludeLabels": ["ubuntu-22.04"]}]:
            with self.subTest(constraints=constraints):
                entries, skipped, summary = self.compose([
                    {"name": "Pinned", "supports": ["linux"], "os": constraints}
                ], tier="pr")
                self.assertEqual(["ubuntu-24.04"], [e["os"] for e in entries])
                self.assertEqual([], skipped)
                self.assertNotIn("Unknown", summary)

    def test_constraints_never_expand_platform_support(self):
        entries, skipped, _ = self.compose([
            {"name": "Linux", "supports": ["linux"], "os": {"includePlatforms": ["windows"]}}
        ])
        self.assertEqual([], entries)
        self.assertEqual(1, len(skipped))

    def test_empty_filtered_inventory_produces_no_jobs(self):
        entries, skipped, _ = self.compose([])
        self.assertEqual([], entries)
        self.assertEqual([], skipped)


if __name__ == "__main__":
    unittest.main()
