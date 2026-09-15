"""Compact profiles and delivered role requirements for eight prepared issues."""

import json
from pathlib import Path
import sys
import unittest
from unittest.mock import patch

from acceptance_profile import ProfileError, profile_for_changes


MANIFEST = Path(__file__).resolve().parents[2] / "scripts/bugfix/issues.json"
sys.path.insert(0, str(MANIFEST.parents[2] / ".github/scripts"))
import collect_bugfix_worker as collector

COUNTS = {1159: (1, False), 1224: (2, True), 2858: (4, True), 2864: (1, True),
          2769: (2, True), 2225: (4, True), 2322: (2, False), 2873: (2, True),
          2860: (4, False), 2870: (4, True)}
NOTES = {1159: {"behavior": "GetMember"},
         1224: {"behavior": "#2869", "compatibility": "BSON Double"},
         2858: {"behavior": "LiteDatabase.Execute", "compatibility": "UserVersion", "lifecycle": "global culture"},
         2864: {"behavior": "INVALID_DATAFILE_STATE", "compatibility": "raw header"},
         2769: {"behavior": "UInt64", "compatibility": "Int32 documents"},
         2225: {"behavior": "fixed Guid", "compatibility": "non-public access"},
         2322: {"behavior": "#2770/#2779"},
         2873: {"behavior": "reported System.Enum", "compatibility": "one-argument Ctor", "lifecycle": "configuration isolation"},
         2860: {"behavior": "OS-dependent /a/b baseline"},
         2870: {"behavior": "original no-inline source frame"}}


class WaveThreeProfiles(unittest.TestCase):
    def definition(self, number):
        return json.loads(MANIFEST.read_text(encoding="utf-8"))["issues"][str(number)]

    def profile(self, number, definition=None):
        contract = definition or self.definition(number)
        return profile_for_changes(contract["allowed_production_paths"], contract,
                                   "a" * 40, "b" * 40, "-old\n+fixed")

    def test_all_profiles_require_the_reviewed_lanes_and_builds(self):
        for number, (lanes, compatibility) in COUNTS.items():
            with self.subTest(issue=number):
                profile = self.profile(number)
                self.assertEqual(lanes, len(profile["required_lanes"]))
                self.assertEqual(compatibility, profile["compatibility"])
                self.assertTrue(profile["production_build"])
                self.assertTrue(profile["targeted_tests_covered_by_broad"])
                self.assertNotIn("full-matrix", profile["checks"])
                self.assertEqual(sorted(self.definition(number)["required_environments"]),
                                 profile["required_environments"])

    def test_additional_unknown_path_gets_conservative_fallback(self):
        for number in COUNTS:
            definition = self.definition(number)
            definition["allowed_production_paths"].append("LiteDB/Other.cs")
            profile = self.profile(number, definition)
            self.assertEqual(6, len(profile["required_lanes"]))
            self.assertTrue(profile["compatibility"])

    def test_required_environments_must_remain_approved(self):
        for number in COUNTS:
            definition = self.definition(number)
            definition["environments"].remove(definition["required_environments"][-1])
            with self.subTest(issue=number), self.assertRaisesRegex(ProfileError, "explicitly approved"):
                self.profile(number, definition)

    def test_environment_aware_issues_use_the_four_reviewed_linux_windows_lanes(self):
        expected = [
            {"os": "ubuntu-latest", "framework": "net8.0"},
            {"os": "ubuntu-latest", "framework": "net10.0"},
            {"os": "windows-latest", "framework": "net8.0"},
            {"os": "windows-latest", "framework": "net10.0"},
        ]
        for number in (2860, 2870):
            with self.subTest(issue=number):
                self.assertEqual(expected, self.profile(number)["matrix"])

    def test_role_notes_are_in_task_contract_and_bound_by_hashes(self):
        for number, roles in NOTES.items():
            contract = self.definition(number)
            task = json.loads(json.dumps({"contract": contract}))
            self.assertEqual(set(roles), set(task["contract"]["review_requirements"]))
            for role, phrase in roles.items():
                self.assertIn(phrase, " ".join(task["contract"]["review_requirements"][role]))
            original = self.profile(number, contract)
            del contract["review_requirements"]
            changed = self.profile(number, contract)
            self.assertNotEqual(original["contract_sha256"], changed["contract_sha256"])
            self.assertNotEqual(original["profile_sha256"], changed["profile_sha256"])

    def test_uint64_notes_cover_legacy_types_and_existing_double_callers(self):
        text = " ".join(self.definition(1224)["review_requirements"]["compatibility"])
        for phrase in ("fit Int64", "round-trip", "index", "equality", "ordering",
                       "converting those BsonValues to double", "#2869", "precision loss"):
            self.assertIn(phrase, text)

    def test_full_collector_contract_delivers_notes_to_each_assigned_role(self):
        for number, roles in NOTES.items():
            contract = self.definition(number)
            for role in ("fix", *roles):
                expected = {"issue": number, "base_sha": "a" * 40,
                            "test_source_sha": contract["frozen_test_revision"]}
                if role != "fix":
                    expected.update({"role": role, "candidate_sha": "b" * 40})

                def source_git(repo, *args):
                    if args == ("rev-parse", "HEAD"):
                        return expected.get("candidate_sha", expected["base_sha"]).encode()
                    if args[:2] == ("hash-object", "--"):
                        return contract["frozen_test_blobs"][args[2]].encode()
                    self.assertEqual(("ls-files", "--others", "--exclude-standard"), args)
                    return b""

                with self.subTest(issue=number, role=role), patch.object(collector, "git", source_git):
                    validated = collector.validate_contract(MANIFEST.parent,
                        {"schema_version": 1, "issues": {str(number): contract}}, expected)
                    self.assertEqual(contract["review_requirements"], validated["review_requirements"])


if __name__ == "__main__":
    unittest.main()
