import json
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

from apply_issue_2794_harness_overlay import apply_overlay, load_manifest_path, OverlayError


SOURCE_PATH = "LiteDB.ReproRunner/Repros/Issue_2794_SharedJobHandoff/Worker/Program.cs"
OVERLAY_PATH = ".github/bugfix/issue-2794-worker-overlay.cs"
MANIFEST_PATH = ".github/bugfix/issue-2794-harness-overlay.json"


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


class Issue2794HarnessOverlayTests(unittest.TestCase):
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

    def test_applies_only_reviewed_worker_blob_and_emits_provenance(self):
        output = self.root / "provenance.json"
        result = apply_overlay(
            self.source, self.definition, self.source_sha, self.definition_sha,
            "windows-2022", output)

        self.assertEqual([SOURCE_PATH], git(self.source, "diff", "--name-only").splitlines())
        self.assertEqual(git(self.definition, "rev-parse", f"HEAD:{OVERLAY_PATH}"),
                         git(self.source, "hash-object", SOURCE_PATH))
        self.assertEqual(result, json.loads(output.read_text(encoding="utf-8")))
        self.assertEqual(self.source_sha, result["source_sha"])
        self.assertEqual(self.definition_sha, result["evidence_definition_sha"])
        self.assertEqual(60, result["wait_timeout_seconds"])
        self.assertEqual(15, result["ready_timeout_seconds"])
        self.assertEqual(90, result["completion_timeout_seconds"])

    def test_rejects_source_with_an_unreviewed_tracked_change(self):
        (self.source / SOURCE_PATH).write_text("changed\n", encoding="utf-8")
        with self.assertRaisesRegex(OverlayError, "tracked changes"):
            apply_overlay(
                self.source, self.definition, self.source_sha, self.definition_sha,
                "windows-2022", self.root / "provenance.json")

    def test_rejects_malformed_dependency_hashes(self):
        manifest = json.loads(Path(MANIFEST_PATH).read_text(encoding="utf-8"))
        manifest["dependency_blobs"] = list(manifest["dependency_blobs"])
        path = self.root / "malformed.json"
        path.write_text(json.dumps(manifest), encoding="utf-8")
        with self.assertRaisesRegex(OverlayError, "dependency set"):
            load_manifest_path(path)


if __name__ == "__main__":
    unittest.main()
