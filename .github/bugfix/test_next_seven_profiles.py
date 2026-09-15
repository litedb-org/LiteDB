"""The later wave requires its reviewed runtime, platform and compatibility lanes."""

import json
from pathlib import Path
import unittest

from acceptance_profile import ProfileError, profile_for_changes


MANIFEST = Path(__file__).resolve().parents[2] / "scripts/bugfix/issues.json"
LINUX = "bugfix-check-ubuntu-latest-"
WINDOWS = "bugfix-check-windows-latest-"
EXPECTED = {
    2770: ([LINUX + "net8.0", LINUX + "net10.0"], False),
    2867: ([LINUX + "net8.0", LINUX + "net10.0"], True),
    2845: ([LINUX + "net8.0", LINUX + "net10.0"], True),
    2847: ([LINUX + "net8.0", WINDOWS + "net8.0"], False),
    2779: ([LINUX + "net8.0", LINUX + "net10.0"], False),
    2205: ([LINUX + "net8.0"], False),
    2871: ([LINUX + "net8.0", LINUX + "net10.0",
            WINDOWS + "net8.0", WINDOWS + "net10.0"], False),
}


class NextSevenProfiles(unittest.TestCase):
    def definition(self, number):
        return json.loads(MANIFEST.read_text(encoding="utf-8"))["issues"][str(number)]

    def profile(self, number, definition=None, paths=None, diff="-old\n+fixed"):
        definition = definition or self.definition(number)
        return profile_for_changes(paths or definition["allowed_production_paths"], definition,
                                   "a" * 40, "b" * 40, diff)

    def test_each_pair_preserves_explicit_lanes_and_compatibility(self):
        for number, (lanes, compatibility) in EXPECTED.items():
            definition = self.definition(number)
            selections = [definition["allowed_production_paths"]]
            selections += [[path] for path in definition["allowed_production_paths"]]
            for paths in selections:
                with self.subTest(issue=number, paths=paths):
                    profile = self.profile(number, paths=paths)
                    self.assertEqual(lanes, profile["required_lanes"])
                    self.assertEqual(compatibility, profile["compatibility"])
                    self.assertTrue(profile["production_build"])
                    self.assertTrue(profile["targeted_tests_covered_by_broad"])
                    self.assertEqual(compatibility, bool(profile["targeted_test_filters"]))
                    self.assertNotIn("full-matrix", profile["checks"])
                    self.assertEqual(sorted(definition["required_environments"]),
                                     profile["required_environments"])

    def test_contract_must_approve_each_required_runtime_and_platform(self):
        for number in EXPECTED:
            original = self.definition(number)
            for environment in original["required_environments"]:
                definition = self.definition(number)
                definition["environments"].remove(environment)
                with self.subTest(issue=number, environment=environment):
                    with self.assertRaisesRegex(ProfileError, "explicitly approved"):
                        self.profile(number, definition=definition)

    def test_other_issue_does_not_inherit_curated_path_exceptions(self):
        for number in EXPECTED:
            if number == 2845:
                continue  # JSON is covered by the general serialization rule.
            definition = self.definition(number)
            definition["inventory_issue"] = 9999
            with self.subTest(issue=number):
                profile = self.profile(number, definition=definition)
                self.assertEqual(6, len(profile["required_lanes"]))
                self.assertTrue(profile["compatibility"])

    def test_unknown_additional_path_is_rejected_or_receives_conservative_fallback(self):
        for number in EXPECTED:
            definition = self.definition(number)
            paths = definition["allowed_production_paths"] + ["LiteDB/Other.cs"]
            with self.subTest(issue=number):
                with self.assertRaisesRegex(ProfileError, "approved issue scope"):
                    self.profile(number, paths=paths)
                definition["allowed_production_paths"] = paths
                profile = self.profile(number, definition=definition)
                self.assertEqual(6, len(profile["required_lanes"]))
                self.assertTrue(profile["compatibility"])

    def test_sensitive_diff_can_widen_every_prepared_pair(self):
        for number, (_, compatibility) in EXPECTED.items():
            with self.subTest(issue=number):
                profile = self.profile(number, diff="+OperatingSystem.IsWindows()\n+#if NET8_0_OR_GREATER")
                self.assertEqual([LINUX + "net8.0", LINUX + "net10.0",
                                  WINDOWS + "net8.0", WINDOWS + "net10.0"], profile["required_lanes"])
                self.assertEqual(compatibility, profile["compatibility"])


if __name__ == "__main__":
    unittest.main()
