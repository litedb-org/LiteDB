"""The per-fix profile widens from trusted source evidence and never worker claims."""

from pathlib import Path
import subprocess
import tempfile
import unittest

from acceptance_profile import (POLICY, ProfileError, digest, plan,
                                profile_for_changes)


ENVIRONMENTS = [f"{prefix}-{framework}" for prefix in
                ("linux-x64", "windows-x64", "macos-x64", "macos-arm64")
                for framework in ("net8.0", "net10.0")]


def contract(issue=2874):
    return {"inventory_issue": issue, "environments": ENVIRONMENTS.copy(),
            "allowed_production_paths": [
                "LiteDB/Document/ObjectId.cs", "LiteDB/Document/BsonValue.cs",
                "LiteDB/Client/Database/Collections/Aggregate.cs", "LiteDB/Other.cs",
                "LiteDB/Client/Mapper/NewBehavior.cs", "LiteDB/Engine/Services/WalIndexService.cs",
                "LiteDB/Document/Bson/BsonSerializer.cs", "LiteDB/Engine/Engine/Upgrade.cs",
                "LiteDB/Engine/Disk/Streams/AesStream.cs", "LiteDB/Engine/Services/VectorIndexService.cs"]}


class ProfileSelectionTests(unittest.TestCase):
    def profile(self, paths, issue=2874, diff="-old\n+fixed", definition=None):
        return profile_for_changes(paths, definition or contract(issue), "a" * 40, "b" * 40, diff)

    def test_three_reviewed_ordinary_pairs_use_one_linux_lane(self):
        for issue, path in ((2874, "LiteDB/Document/ObjectId.cs"),
                            (2839, "LiteDB/Client/Database/Collections/Aggregate.cs"),
                            (2869, "LiteDB/Document/BsonValue.cs")):
            with self.subTest(issue=issue):
                profile = self.profile([path], issue)
                self.assertEqual([{"os": "ubuntu-latest", "framework": "net8.0"}], profile["matrix"])
                self.assertEqual(["bugfix-check-ubuntu-latest-net8.0"], profile["required_lanes"])
                self.assertFalse(profile["compatibility"])
                self.assertTrue(profile["production_build"])
                self.assertEqual([], profile["targeted_test_filters"])

    def test_another_issue_in_same_file_does_not_inherit_small_profile(self):
        profile = self.profile(["LiteDB/Document/ObjectId.cs"], issue=9999)
        self.assertEqual(6, len(profile["required_lanes"]))
        self.assertTrue(profile["compatibility"])

    def test_approved_environments_do_not_mean_required_environments(self):
        definition = contract()
        definition["required_environments"] = ["windows-x64-net10.0"]
        profile = self.profile(["LiteDB/Document/ObjectId.cs"], definition=definition)
        self.assertEqual(["bugfix-check-ubuntu-latest-net8.0", "bugfix-check-windows-latest-net10.0"],
                         profile["required_lanes"])

    def test_platform_only_contract_selects_its_approved_baseline(self):
        definition = contract()
        definition["environments"] = ["windows-x64-net8.0"]
        profile = self.profile(["LiteDB/Document/ObjectId.cs"], definition=definition)
        self.assertEqual(["bugfix-check-windows-latest-net8.0"], profile["required_lanes"])

    def test_runtime_sensitive_diff_adds_net10(self):
        profile = self.profile(["LiteDB/Document/ObjectId.cs"], diff="+#if NET8_0_OR_GREATER\n+fixed\n+#endif")
        self.assertEqual(["bugfix-check-ubuntu-latest-net8.0", "bugfix-check-ubuntu-latest-net10.0"],
                         profile["required_lanes"])

    def test_platform_sensitive_diff_adds_windows(self):
        profile = self.profile(["LiteDB/Document/ObjectId.cs"], diff="+OperatingSystem.IsWindows()")
        self.assertEqual(["bugfix-check-ubuntu-latest-net8.0", "bugfix-check-windows-latest-net8.0"],
                         profile["required_lanes"])

    def test_storage_serialization_upgrade_encryption_and_vector_require_compatibility(self):
        paths = ["LiteDB/Engine/Services/WalIndexService.cs", "LiteDB/Document/Bson/BsonSerializer.cs",
                 "LiteDB/Engine/Engine/Upgrade.cs", "LiteDB/Engine/Disk/Streams/AesStream.cs",
                 "LiteDB/Engine/Services/VectorIndexService.cs"]
        for path in paths:
            with self.subTest(path=path):
                profile = self.profile([path], issue=9999)
                self.assertTrue(profile["compatibility"])
                self.assertTrue(profile["production_build"])
                self.assertTrue(profile["targeted_test_filters"])
                self.assertTrue(profile["targeted_tests_covered_by_broad"])
                self.assertLess(len(profile["required_lanes"]), 6)

    def test_unknown_path_widens_all_six_lanes_without_large_matrix(self):
        profile = self.profile(["LiteDB/Client/Mapper/NewBehavior.cs"])
        self.assertEqual(6, len(profile["matrix"]))
        self.assertTrue(profile["compatibility"])
        self.assertNotIn("full-matrix", profile["checks"])

    def test_unknown_path_cannot_be_hidden_by_known_ordinary_path(self):
        profile = self.profile(["LiteDB/Document/ObjectId.cs", "LiteDB/Other.cs"])
        self.assertEqual(6, len(profile["matrix"]))

    def test_required_or_selected_unapproved_environment_rejected(self):
        definition = contract()
        definition["environments"] = ["linux-x64-net8.0"]
        with self.assertRaisesRegex(ProfileError, "exceed"):
            self.profile(["LiteDB/Other.cs"], definition=definition)
        definition["required_environments"] = ["windows-x64-net8.0"]
        with self.assertRaisesRegex(ProfileError, "explicitly approved"):
            self.profile(["LiteDB/Document/ObjectId.cs"], definition=definition)

    def test_explicit_macos_architecture_cannot_be_promised_by_generic_runner(self):
        for architecture in ("x64", "arm64"):
            definition = contract()
            definition["required_environments"] = [f"macos-{architecture}-net8.0"]
            with self.subTest(architecture=architecture), self.assertRaisesRegex(ProfileError, "dedicated runner"):
                self.profile(["LiteDB/Document/ObjectId.cs"], definition=definition)

    def test_worker_cannot_expand_approved_production_scope(self):
        definition = contract()
        definition["allowed_production_paths"] = ["LiteDB/Document/ObjectId.cs"]
        with self.assertRaisesRegex(ProfileError, "approved issue scope"):
            self.profile(["LiteDB/Document/BsonValue.cs"], definition=definition)

    def test_digest_binds_diff_contract_commits_and_selection(self):
        profile = self.profile(["LiteDB/Document/ObjectId.cs"])
        unsigned = {key: value for key, value in profile.items() if key != "profile_sha256"}
        self.assertEqual(digest(unsigned), profile["profile_sha256"])
        changed = self.profile(["LiteDB/Document/ObjectId.cs"], diff="-old\n+different")
        self.assertNotEqual(profile["profile_sha256"], changed["profile_sha256"])
        reordered = contract()
        reordered["required_environments"] = ["windows-x64-net8.0"]
        changed = self.profile(["LiteDB/Document/ObjectId.cs"], definition=reordered)
        self.assertNotEqual(profile["profile_sha256"], changed["profile_sha256"])
        self.assertEqual(64, len(profile["policy_sha256"]))

    def test_path_order_and_line_endings_do_not_change_profile(self):
        paths = ["LiteDB/Engine/Engine/Upgrade.cs", "LiteDB/Engine/Disk/Streams/AesStream.cs"]
        self.assertEqual(self.profile(paths), self.profile(list(reversed(paths))))
        self.assertEqual(self.profile(paths, diff="-old\n+fixed\n"),
                         self.profile(paths, diff="-old\r\n+fixed\r\n"))

    def test_invalid_paths_empty_diff_and_nonimmutable_sha_rejected(self):
        for paths in ([], ["LiteDB/../scripts/x.py"], ["scripts/bugfix/x.py"], ["LiteDB//Other.cs"],
                      ["LiteDB/Other.cs", "LiteDB/Other.cs"], [["invalid"]]):
            with self.subTest(paths=paths), self.assertRaises(ProfileError):
                self.profile(paths)
        with self.assertRaisesRegex(ProfileError, "diff"):
            self.profile(["LiteDB/Document/ObjectId.cs"], diff="")
        with self.assertRaisesRegex(ProfileError, "immutable"):
            profile_for_changes(["LiteDB/Other.cs"], contract(), "HEAD", "b" * 40, "diff")

    def test_coverage_filters_reference_existing_frozen_test_classes(self):
        root = Path(__file__).resolve().parents[2]
        definitions = set()
        for rule in POLICY["rules"]:
            definitions.update(rule["tests"])
        files = subprocess.check_output(["git", "-C", str(root), "ls-tree", "-r", "--name-only",
                                         "dd937719f7eee53c512f50ac604cab639bf42a4c", "--", "LiteDB.Tests"], text=True)
        for name in definitions:
            class_name = name.rsplit(".", 1)[1]
            matching = [path for path in files.splitlines() if path.endswith("/" + class_name + ".cs")]
            self.assertEqual(1, len(matching), name)
            source = subprocess.check_output(["git", "-C", str(root), "show",
                                              "dd937719f7eee53c512f50ac604cab639bf42a4c:" + matching[0]], text=True)
            self.assertIn("namespace " + name.rsplit(".", 1)[0], source)
            self.assertIn("class " + class_name, source)


class ImmutableGitTests(unittest.TestCase):
    def test_working_tree_changes_cannot_change_a_committed_candidate_profile(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            def git(*arguments):
                return subprocess.check_output(["git", "-C", str(root), *arguments], text=True).strip()
            git("init", "--quiet")
            git("config", "user.name", "Profile fixture")
            git("config", "user.email", "fixture@example.invalid")
            git("config", "core.autocrlf", "false")
            source = root / "LiteDB/Document/ObjectId.cs"
            source.parent.mkdir(parents=True)
            source.write_text("original\n", encoding="utf-8")
            git("add", ".")
            git("-c", "core.hooksPath=", "-c", "commit.gpgsign=false", "commit", "--quiet", "-m", "Base fixture")
            baseline = git("rev-parse", "HEAD")
            source.write_text("fixed\n", encoding="utf-8")
            git("add", ".")
            git("-c", "core.hooksPath=", "-c", "commit.gpgsign=false", "commit", "--quiet", "-m", "Candidate fixture")
            candidate = git("rev-parse", "HEAD")
            profile = plan(root, baseline, candidate, contract())
            source.write_text("OperatingSystem.IsWindows();\n#if NET10_0_OR_GREATER\n", encoding="utf-8")
            self.assertEqual(profile, plan(root, baseline, candidate, contract()))
            self.assertEqual(1, len(profile["required_lanes"]))
            with self.assertRaisesRegex(ProfileError, "descend"):
                plan(root, candidate, baseline, contract())
            with self.assertRaises(subprocess.CalledProcessError):
                plan(root, baseline, "f" * 40, contract())
            git("config", "diff.noprefix", "true")
            git("config", "diff.algorithm", "histogram")
            git("config", "core.abbrev", "10")
            self.assertEqual(profile, plan(root, baseline, candidate, contract()))


if __name__ == "__main__":
    unittest.main()
