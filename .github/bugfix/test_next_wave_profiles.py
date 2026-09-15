"""Reviewed issue/path pairs retain compact validation and compatibility checks."""

import copy
import json
from pathlib import Path
import subprocess
import unittest

from acceptance_profile import POLICY, ProfileError, profile_for_changes


ROOT = Path(__file__).resolve().parents[2]
MANIFEST = ROOT / "scripts/bugfix/issues.json"
FROZEN = "dd937719f7eee53c512f50ac604cab639bf42a4c"


class NextWaveProfiles(unittest.TestCase):
    def definition(self, number):
        return json.loads(MANIFEST.read_text(encoding="utf-8"))["issues"][str(number)]

    def profile(self, number, diff="-old\n+fixed", definition=None, paths=None):
        contract = definition or self.definition(number)
        return profile_for_changes(paths or contract["allowed_production_paths"], contract,
                                   "a" * 40, "b" * 40, diff)

    def test_each_next_issue_uses_one_lane_and_required_compatibility(self):
        for number in (1506, 1002, 2802):
            with self.subTest(issue=number):
                profile = self.profile(number)
                self.assertEqual([{"os": "ubuntu-latest", "framework": "net8.0"}], profile["matrix"])
                self.assertEqual(["bugfix-check-ubuntu-latest-net8.0"], profile["required_lanes"])
                self.assertEqual(number != 1506, profile["compatibility"])
                self.assertTrue(profile["production_build"])
                self.assertEqual(["focused", "broad", "production-build"], profile["checks"])
                self.assertTrue(profile["targeted_tests_covered_by_broad"])
                self.assertEqual(number != 1506, bool(profile["targeted_test_filters"]))

    def test_other_issues_in_the_same_files_receive_conservative_profiles(self):
        for number in (1506, 1002, 2802):
            definition = self.definition(number)
            definition["inventory_issue"] = 9999
            with self.subTest(issue=number):
                profile = self.profile(number, definition=definition)
                self.assertEqual(6, len(profile["required_lanes"]))
                self.assertTrue(profile["compatibility"])

    def test_scope_expansion_cannot_silently_keep_a_compact_profile(self):
        for number in (1506, 1002, 2802):
            definition = self.definition(number)
            paths = definition["allowed_production_paths"] + ["LiteDB/Client/Structures/Query.cs"]
            with self.subTest(issue=number), self.assertRaisesRegex(ProfileError, "approved issue scope"):
                self.profile(number, paths=paths)
            expanded = copy.deepcopy(definition)
            expanded["allowed_production_paths"] = paths
            profile = self.profile(number, definition=expanded)
            self.assertEqual(6, len(profile["required_lanes"]))
            self.assertTrue(profile["compatibility"])

    def test_platform_and_runtime_evidence_can_widen_each_curated_pair(self):
        for number in (1506, 1002, 2802):
            with self.subTest(issue=number):
                profile = self.profile(number, diff="+OperatingSystem.IsWindows()\n+#if NET8_0_OR_GREATER")
                self.assertEqual(4, len(profile["required_lanes"]))
                self.assertEqual(number != 1506, profile["compatibility"])

    def test_compatibility_coverage_classes_exist_in_frozen_sources(self):
        for rule in POLICY["compatibility_pairs"].values():
            for name in rule["tests"]:
                namespace, class_name = name.rsplit(".", 1)
                relative = namespace.removeprefix("LiteDB.Tests.").replace(".", "/")
                path = f"LiteDB.Tests/{relative}/{class_name}.cs"
                source = subprocess.check_output(
                    ["git", "-C", str(ROOT), "show", f"{FROZEN}:{path}"], text=True)
                self.assertIn("namespace " + namespace, source)
                self.assertIn("class " + class_name, source)
                self.assertTrue("[Fact" in source or "[Theory" in source)


if __name__ == "__main__":
    unittest.main()
