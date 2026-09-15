"""Material review obligations travel inside the immutable worker task contract."""

import json
from pathlib import Path
import sys
import unittest
from unittest.mock import patch

from acceptance_profile import profile_for_changes


ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / ".github/scripts"))
import collect_bugfix_worker as collector


REQUIRED = {
    2847: {
        "behavior": ("StringComparison", "NotSupportedException", "pinned base",
                     "OrdinalIgnoreCase", "support loss"),
        "compatibility": ("API compatibility", "unsupported mode", "pinned base",
                          "OrdinalIgnoreCase", "now throw"),
    },
    2867: {
        "compatibility": ("old databases", "former inherited-ID convention", "BSON _id",
                          "normal field", "legacy documents", "migration needs",
                          "does not exercise this mapping"),
    },
    2871: {
        "lifecycle": ("actual synchronization", "reads and writes", "concurrent misses",
                      "non-Dictionary cache bypasses", "not evidence of thread safety",
                      "fresh object creation", "interface recursion", "bounded stress control"),
    },
    2205: {
        "behavior": ("complete out-of-range unquoted numeric token", "exact string value",
                     "another exception", "truncating", "clamping", "prefix/suffix",
                     "quoted and parameterized", "numeric boundaries"),
    },
}


class ReviewRequirements(unittest.TestCase):
    def manifest(self):
        return json.loads((ROOT / "scripts/bugfix/issues.json").read_text(encoding="utf-8"))

    def test_each_material_obligation_is_in_its_role_contract(self):
        for number, roles in REQUIRED.items():
            requirements = self.manifest()["issues"][str(number)]["review_requirements"]
            self.assertEqual(set(roles), set(requirements))
            for role, phrases in roles.items():
                with self.subTest(issue=number, role=role):
                    self.assertIsInstance(requirements[role], list)
                    self.assertTrue(requirements[role])
                    self.assertTrue(all(isinstance(note, str) and note.strip()
                                        for note in requirements[role]))
                    text = " ".join(requirements[role])
                    for phrase in phrases:
                        self.assertIn(phrase, text)

    def test_collector_and_task_json_preserve_all_role_notes(self):
        manifest = self.manifest()
        for number in REQUIRED:
            contract = manifest["issues"][str(number)]
            for role in ("fix", "behavior", "compatibility", "lifecycle"):
                expected = {"schema_version": 1, "issue": number, "base_sha": "a" * 40,
                            "test_source_sha": contract["frozen_test_revision"]}
                if role != "fix":
                    expected.update({"candidate_sha": "b" * 40, "role": role})

                def source_git(repo, *args):
                    if args == ("rev-parse", "HEAD"):
                        return expected.get("candidate_sha", expected["base_sha"]).encode()
                    if args[:2] == ("hash-object", "--"):
                        return contract["frozen_test_blobs"][args[2]].encode()
                    self.assertEqual(("ls-files", "--others", "--exclude-standard"), args)
                    return b""

                with self.subTest(issue=number, role=role), patch.object(collector, "git", source_git):
                    validated = collector.validate_contract(ROOT, manifest, expected)
                    task = json.loads(json.dumps({"identity": expected, "contract": validated}))
                    self.assertEqual(contract, task["contract"])
                    self.assertEqual(contract["review_requirements"],
                                     task["contract"]["review_requirements"])

    def test_review_notes_are_bound_by_contract_and_profile_digests(self):
        for number in REQUIRED:
            contract = self.manifest()["issues"][str(number)]
            original = profile_for_changes(contract["allowed_production_paths"], contract,
                                           "a" * 40, "b" * 40, "-old\n+fixed")
            del contract["review_requirements"]
            without_notes = profile_for_changes(contract["allowed_production_paths"], contract,
                                                "a" * 40, "b" * 40, "-old\n+fixed")
            with self.subTest(issue=number):
                self.assertNotEqual(original["contract_sha256"], without_notes["contract_sha256"])
                self.assertNotEqual(original["profile_sha256"], without_notes["profile_sha256"])
                self.assertEqual(original["required_lanes"], without_notes["required_lanes"])


if __name__ == "__main__":
    unittest.main()
