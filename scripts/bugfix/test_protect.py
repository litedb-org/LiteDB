from pathlib import Path
import subprocess
import tempfile
import unittest

from protect import git, verify_changes, verify_frozen_tests
from trx import GateError


class ProtectionTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.repository = Path(self.temporary.name)
        self.run_git("init", "--quiet")
        self.run_git("config", "user.name", "Gate fixture")
        self.run_git("config", "user.email", "fixture@example.invalid")
        self.run_git("config", "core.autocrlf", "false")
        self.write("LiteDB/Document/ObjectId.cs", "original production\n")
        self.write("LiteDB.Tests/Issues/Original.cs", "original contract\n")
        self.base = self.commit()
        blob = git(self.repository, "rev-parse", self.base + ":LiteDB.Tests/Issues/Original.cs").strip()
        self.issue = {"allowed_production_paths": ["LiteDB/Document/ObjectId.cs"],
                      "frozen_test_revision": self.base,
                      "frozen_test_blobs": {"LiteDB.Tests/Issues/Original.cs": blob}}

    def run_git(self, *arguments):
        return subprocess.run(["git", "-C", str(self.repository), *arguments],
                              check=True, capture_output=True, text=True).stdout.strip()

    def write(self, path, text):
        target = self.repository / path
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(text, encoding="utf-8")

    def commit(self):
        self.run_git("add", ".")
        self.run_git("-c", "commit.gpgsign=false", "-c", "core.hooksPath=", "commit", "--quiet", "-m", "Fixture")
        return self.run_git("rev-parse", "HEAD")

    def test_production_fix_and_additional_test_allowed(self):
        self.write("LiteDB/Document/ObjectId.cs", "fixed production\n")
        self.write("LiteDB.Tests/Issues/Additional.cs", "additional test\n")
        paths = verify_changes(self.repository, self.base, self.commit(), self.issue)
        self.assertEqual(2, len(paths))

    def test_original_test_change_rejected(self):
        self.write("LiteDB.Tests/Issues/Original.cs", "weakened contract\n")
        with self.assertRaisesRegex(GateError, "Original regression"):
            verify_changes(self.repository, self.base, self.commit(), self.issue)

    def test_grader_configuration_change_rejected(self):
        self.write("scripts/bugfix/issues.json", "{}")
        with self.assertRaisesRegex(GateError, "protected"):
            verify_changes(self.repository, self.base, self.commit(), self.issue)

    def test_disallowed_production_file_rejected(self):
        self.write("LiteDB/Engine/Other.cs", "unreviewed scope\n")
        with self.assertRaisesRegex(GateError, "protected"):
            verify_changes(self.repository, self.base, self.commit(), self.issue)

    def test_unchanged_candidate_rejected(self):
        with self.assertRaisesRegex(GateError, "no changes"):
            verify_changes(self.repository, self.base, self.base, self.issue)

    def test_frozen_source_verified_without_candidate(self):
        self.assertEqual(["LiteDB.Tests/Issues/Original.cs"],
                         verify_frozen_tests(self.repository, self.base, self.issue))


if __name__ == "__main__":
    unittest.main()
