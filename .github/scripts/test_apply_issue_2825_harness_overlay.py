import json
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

from apply_issue_2794_harness_overlay import OverlayError
from apply_issue_2825_harness_overlay import (
    apply_overlay,
    load_manifest_path,
    transformed_classifier,
)


SOURCE_PATH = "LiteDB.ReproRunner/Repros/Issue_2825_FreeListRace/FreeListFailureClassifier.cs"
OVERLAY_PATH = ".github/bugfix/issue-2825-classifier-overlay.cs"
MANIFEST_PATH = ".github/bugfix/issue-2825-harness-overlay.json"


def git(root, *arguments, binary=False):
    result = subprocess.run(
        ["git", "-C", str(root), *arguments], check=True,
        stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    return result.stdout if binary else result.stdout.decode().strip()


def initialize(root):
    git(root, "init", "--quiet")
    git(root, "config", "user.email", "test@example.invalid")
    git(root, "config", "user.name", "Test")
    git(root, "config", "core.autocrlf", "false")


class Issue2825HarnessOverlayTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)
        self.source = self.root / "source"
        self.definition = self.root / "definition"
        self.source.mkdir()
        self.definition.mkdir()
        initialize(self.source)
        initialize(self.definition)

        manifest = json.loads(Path(MANIFEST_PATH).read_text(encoding="utf-8"))
        for relative in [SOURCE_PATH, *manifest["dependency_blobs"]]:
            source_file = self.source / relative
            source_file.parent.mkdir(parents=True, exist_ok=True)
            source_file.write_bytes(
                git(Path.cwd(), "show", f"HEAD:{relative}", binary=True))
        git(self.source, "add", ".")
        git(self.source, "commit", "--quiet", "-m", "source")
        self.source_sha = git(self.source, "rev-parse", "HEAD")

        for relative in (OVERLAY_PATH, MANIFEST_PATH):
            destination = self.definition / relative
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(relative, destination)
        git(self.definition, "add", ".")
        git(self.definition, "commit", "--quiet", "-m", "definition")
        self.definition_sha = git(self.definition, "rev-parse", "HEAD")

    def tearDown(self):
        self.temporary.cleanup()

    def test_applies_only_reviewed_classifier_blob_and_emits_provenance(self):
        output = self.root / "provenance.json"
        result = apply_overlay(
            self.source, self.definition, self.source_sha, self.definition_sha,
            "ubuntu-22.04", output)

        self.assertEqual([SOURCE_PATH], git(self.source, "diff", "--name-only").splitlines())
        self.assertEqual(git(self.definition, "rev-parse", f"HEAD:{OVERLAY_PATH}"),
                         git(self.source, "hash-object", SOURCE_PATH))
        self.assertEqual(result, json.loads(output.read_text(encoding="utf-8")))
        self.assertEqual(2825, result["issue"])
        self.assertEqual(self.source_sha, result["source_sha"])
        self.assertEqual(self.definition_sha, result["evidence_definition_sha"])

    def test_exact_transform_rejects_changed_or_missing_source_branch(self):
        original = git(Path.cwd(), "show", f"HEAD:{SOURCE_PATH}", binary=True)
        self.assertEqual(Path(OVERLAY_PATH).read_bytes(), transformed_classifier(original))
        for changed in (
            original.replace(b"BasePage.InternalInsert", b"BasePage.InternalInsertWrong", 1),
            original.replace(b"IndexService.AddNode", b"IndexService.AddNodeWrong", 1),
        ):
            with self.assertRaisesRegex(OverlayError, "reviewed write branch"):
                transformed_classifier(changed)

    def test_rejects_malformed_manifest_contract(self):
        manifest = json.loads(Path(MANIFEST_PATH).read_text(encoding="utf-8"))
        manifest["secondary_failure"]["adjacent_frames"][1] += "Wrong"
        path = self.root / "malformed.json"
        path.write_text(json.dumps(manifest), encoding="utf-8")
        with self.assertRaisesRegex(OverlayError, "secondary-failure contract"):
            load_manifest_path(path)


if __name__ == "__main__":
    unittest.main()
