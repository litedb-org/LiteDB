import contextlib
import io
import json
import os
import shutil
import subprocess
import tempfile
import unittest
import zipfile
from pathlib import Path

from unittest.mock import patch

import proof_provenance as provenance
import regression_proof as proof
import safety_common as common
from safety_fixtures import GitRepo, csharp_class, run_quietly, script_closure

LEDGER = ".github/safety/regression-proofs.json"
REPRO = "Issue_1_Sample"
FOLDER = f"LiteDB.ReproRunner/Repros/{REPRO}"
GUARD = "LiteDB.Tests/Issues/Issue1_Tests.cs#Guards_the_fix"
BAD_COMMIT = "a" * 40


def csproj(version):
    return (f'<Project><PropertyGroup><LiteDBPackageVersion Condition="\'$(LiteDBPackageVersion)\' == \'\'">'
            f"{version}</LiteDBPackageVersion></PropertyGroup></Project>")


def manifest(**changes):
    value = {"id": REPRO, "title": "Sample", "timeoutSeconds": 60, "requiresParallel": False,
             "defaultInstances": 1, "state": "green"}
    value.update(changes)
    return json.dumps(value)


def ledger(*entries):
    return json.dumps({"proofs": list(entries)})


def entry(**changes):
    value = {"repro": REPRO, "knownBad": {"kind": "package", "version": "5.0.20"}, "permanentGuard": [GUARD]}
    value.update(changes)
    return value


BASE = {
    f"{FOLDER}/repro.json": manifest(),
    f"{FOLDER}/{REPRO}.csproj": csproj("5.0.20"),
    f"{FOLDER}/Program.cs": "return 0;",
    "LiteDB.Tests/Issues/Issue1_Tests.cs": csharp_class("Issue1_Tests", {"Guards_the_fix": ("Fact", "")}),
}


class LedgerTests(unittest.TestCase):
    def run_check(self, files, argv=("validate",), base_files=None):
        with GitRepo() as repo:
            base = repo.commit({**BASE, LEDGER: ledger(), **(base_files or {})})
            repo.commit(files)
            args = list(argv) + (["--base", base] if argv[0] == "select" else [])
            return run_quietly(proof.main, args)

    def test_a_published_package_proof_with_its_guard_is_valid(self):
        code, output = self.run_check({LEDGER: ledger(entry())})
        self.assertEqual(code, 0, output)

    def test_invalid_proofs_are_rejected(self):
        commit = {"kind": "dev-commit", "commit": BAD_COMMIT}
        cases = {
            "a mutant is not a known-bad state": ({}, entry(knownBad={"kind": "mutant"})),
            "LiteDBPackageVersion is '5.0.20'; the known-bad state requires '5.0.21'":
                ({}, entry(knownBad={"kind": "package", "version": "5.0.21"})),
            "explain why no published package": ({f"{FOLDER}/{REPRO}.csproj": csproj("0.0.0-knownbad.aaaaaaaaaaaa")},
                                                 entry(knownBad=commit)),
            "full 40-character commit id": ({}, entry(knownBad={"kind": "dev-commit", "commit": "abc", "reason": "x" * 30})),
            "must be 'green'": ({f"{FOLDER}/repro.json": manifest(state="red")}, entry()),
            "must reproduce deterministically": (
                {f"{FOLDER}/repro.json": manifest(expectedOutcomes={"package": {"kind": "intermittent"}})}, entry()),
            "must expect 'noRepro'": (
                {f"{FOLDER}/repro.json": manifest(expectedOutcomes={"latest": {"kind": "reproduce"}})}, entry()),
            "name the permanent regression guard": ({}, entry(permanentGuard=[])),
            "does not resolve": ({}, entry(permanentGuard=["LiteDB.Tests/Issues/Issue1_Tests.cs#Gone"])),
        }
        for expected, (files, value) in cases.items():
            with self.subTest(expected):
                code, output = self.run_check({**files, LEDGER: ledger(value)})
                self.assertEqual(code, 1)
                self.assertIn(expected, output)

    def test_select_runs_only_added_or_changed_proofs(self):
        code, output = self.run_check({LEDGER: ledger(entry())}, argv=("select",))
        self.assertIn(f"`{REPRO}` (known bad: package 5.0.20)", output)
        code, output = self.run_check({"docs/x.md": "y"}, argv=("select",), base_files={LEDGER: ledger(entry())})
        self.assertIn("none (no regression proof added or changed)", output)
        code, output = self.run_check({f"{FOLDER}/Program.cs": "return 1;"}, argv=("select",),
                                      base_files={LEDGER: ledger(entry())})
        self.assertIn(f"`{REPRO}`", output)
        for harness in ("LiteDB.ReproRunner/LiteDB.ReproRunner.Cli/Evaluator.cs",
                        ".github/workflows/regression-proof.yml", ".github/scripts/repro_scaffold.py"):
            with self.subTest(harness):
                code, output = self.run_check({harness: "# changed"}, argv=("select",),
                                              base_files={LEDGER: ledger(entry())})
                self.assertIn(f"`{REPRO}`", output)  # a harness change re-proves every entry

    def test_the_harness_covers_the_whole_import_closure(self):
        self.assertEqual(script_closure("regression_proof"),
                         {path for path in proof.HARNESS if path.startswith(".github/scripts/")})


class BugPullRequestTests(unittest.TestCase):
    """A bug-fix PR must add (or re-pin) at least one proof."""

    def select_bug_pr(self, base_files, head_files, flags=("--require-new-proof",)):
        with GitRepo() as repo:
            base = repo.commit({**BASE, **base_files})
            repo.commit(head_files)
            return run_quietly(proof.main, ["select", "--base", base, *flags])

    def test_the_bug_fix_labels_require_a_new_proof(self):
        base, head = {LEDGER: ledger()}, {"LiteDB/Fix.cs": "fixed"}
        for labels, expected in (('["bug"]', 1), ('["area: storage", "bugfix-fix"]', 1),
                                 ('["enhancement"]', 0), ("[]", 0), ("null", 0)):
            with self.subTest(labels):
                code, output = self.select_bug_pr(base, head, ("--labels", labels))
                self.assertEqual(code, expected, output)
        with self.assertRaises(SystemExit), contextlib.redirect_stderr(io.StringIO()):
            self.select_bug_pr(base, head, ("--labels", '"bug"'))

    def test_a_bug_pr_without_a_new_proof_fails(self):
        for label, base, head in (
                ("no proofs at all", {LEDGER: ledger()}, {"LiteDB/Fix.cs": "fixed"}),
                ("only an existing proof", {LEDGER: ledger(entry())}, {"LiteDB/Fix.cs": "fixed"}),
                ("only a touched repro", {LEDGER: ledger(entry())}, {f"{FOLDER}/Program.cs": "return 1;"})):
            with self.subTest(label):
                code, output = self.select_bug_pr(base, head)
                self.assertEqual(code, 1)
                self.assertIn("must add at least 1 regression proof", output)

    def test_a_bug_pr_with_a_new_or_repinned_proof_passes_selection(self):
        repinned = {LEDGER: ledger(entry(knownBad={"kind": "package", "version": "5.0.21"})),
                    f"{FOLDER}/{REPRO}.csproj": csproj("5.0.21")}
        for label, base, head in (("new proof", {LEDGER: ledger()}, {LEDGER: ledger(entry())}),
                                  ("re-pinned proof", {LEDGER: ledger(entry())}, repinned)):
            with self.subTest(label):
                code, output = self.select_bug_pr(base, head)
                self.assertEqual(code, 0, output)
                self.assertIn(f"`{REPRO}`", output)


class ScaffoldTests(unittest.TestCase):
    def test_new_creates_a_valid_pinned_repro_that_fails_until_written(self):
        with GitRepo() as repo:
            repo.commit({**BASE, LEDGER: json.dumps({"schemaVersion": 1, "description": "d", "proofs": [entry()]})})
            code, output = run_quietly(proof.main, [
                "new", "--id", "Issue_42_Lost_update", "--issue", "42", "--title", "Update is lost",
                "--known-bad", "package:5.0.21", "--guard", GUARD])
            self.assertEqual(code, 0, output)
            folder = repo.path / "LiteDB.ReproRunner/Repros/Issue_42_Lost_update"
            self.assertIn(">5.0.21</LiteDBPackageVersion>", (folder / "Issue_42_Lost_update.csproj").read_text())
            self.assertIn("throw new NotImplementedException", (folder / "Program.cs").read_text())
            self.assertIn("ReproConfigurationReporter.SendConfiguration(host)", (folder / "Program.cs").read_text())
            data = json.loads((repo.path / LEDGER).read_text())
            self.assertEqual([item["repro"] for item in data["proofs"]], [REPRO, "Issue_42_Lost_update"])
            self.assertEqual(data["description"], "d")
            code, output = run_quietly(proof.main, ["validate", "--head", common.WORKTREE])
            self.assertEqual(code, 0, output)

    def test_invalid_known_bad_inputs_are_usage_errors(self):
        with GitRepo() as repo:
            repo.commit(BASE)
            for value in ("pr-commit:HEAD", "pr-commit:HEAD@abc", "dev-commit:not-a-commit", "package:", "mutant:x"):
                with self.subTest(value), self.assertRaises(SystemExit) as raised:
                    proof.parse_known_bad(value, "reason")
                self.assertIn("--known-bad must be", str(raised.exception.code))

    def test_new_rejects_ids_that_do_not_name_an_issue(self):
        with GitRepo() as repo:
            repo.commit(BASE)
            with self.assertRaises(SystemExit):
                run_quietly(proof.main, ["new", "--id", "MyRepro", "--issue", "1", "--title", "t",
                                         "--known-bad", "package:5.0.21"])


class ProvenanceTests(unittest.TestCase):
    def test_commit_states_must_match_their_classification(self):
        with GitRepo() as repo:
            on_dev = repo.commit({"a.txt": "1"})
            repo._git("branch", "dev")
            repo._git("checkout", "-q", "-b", "feature")
            off_dev = repo.commit({"a.txt": "2"})
            repo._git("update-ref", "refs/proof/pr-7", off_dev)
            cases = [
                ({"kind": "dev-commit", "commit": on_dev}, None),
                ({"kind": "dev-commit", "commit": off_dev}, "never existed on dev"),
                ({"kind": "pr-commit", "commit": off_dev, "pr": 7}, None),
                ({"kind": "pr-commit", "commit": on_dev, "pr": 7}, "classify it as dev-commit"),
                ({"kind": "pr-commit", "commit": off_dev, "pr": 8}, "is not part of PR #8"),
            ]
            for bad, expected in cases:
                with self.subTest(bad=bad):
                    report = common.Report("")
                    run_quietly(lambda _: proof.check_provenance({"repro": REPRO, "knownBad": bad}, report,
                                                                 "dev", offline=True), None)
                    if expected is None:
                        self.assertEqual(report.errors, [])
                    else:
                        self.assertTrue(any(expected in error for error in report.errors), report.errors)


FORK = "someone/LiteDB"


class ForkProvenanceTests(unittest.TestCase):
    """A pr-commit of a fork's PR names the fork; without it the PR number means upstream's PR."""

    def setUp(self):
        self.host = Path(tempfile.mkdtemp(prefix="litedb-git-host-"))
        self.addCleanup(shutil.rmtree, self.host, ignore_errors=True)
        patcher = patch.object(provenance, "GIT_HOST", self.host.as_uri() + "/")
        patcher.start()
        self.addCleanup(patcher.stop)

    def upstream_and_fork(self, repo):
        """Upstream PR #133 holds one commit; the fork's PR #133 another. Returns (upstream pr, fork pr) commits."""
        base = repo.commit({"a.txt": "1"})
        repo._git("branch", "dev")
        repo._git("checkout", "-q", "-b", "upstream-pr")
        upstream_pr = repo.commit({"a.txt": "upstream"})
        repo._git("update-ref", "refs/pull/133/head", upstream_pr)
        repo._git("checkout", "-q", "dev")
        repo._git("remote", "add", "origin", str(repo.path))  # the clone's upstream is itself
        fork = self.host / FORK.split("/")[0] / f"{FORK.split('/')[1]}.git"
        fork.mkdir(parents=True)
        git = GitRepo.__new__(GitRepo)
        git.path = fork
        git._git("init", "-q", "-b", "main")
        git._git("fetch", "-q", str(repo.path), "dev:refs/heads/base")
        git._git("checkout", "-q", "-B", "main", "base")
        (fork / "a.txt").write_text("fork")
        git._git("commit", "-q", "-am", "fork change")
        fork_pr = git._git("rev-parse", "HEAD")
        git._git("update-ref", "refs/pull/133/head", fork_pr)
        return upstream_pr, fork_pr

    def check(self, bad, offline=False):
        report = common.Report("")
        run_quietly(lambda _: provenance.check_commit(bad, "Proof", report, "dev", offline), None)
        return report

    def test_the_upstream_default_is_unchanged(self):
        bad = {"kind": "pr-commit", "commit": BAD_COMMIT, "pr": 7}
        self.assertEqual((provenance.proof_ref(bad), provenance.source(bad)), ("refs/proof/pr-7", "origin"))
        with GitRepo() as repo:
            upstream_pr, _ = self.upstream_and_fork(repo)
            report = self.check({"kind": "pr-commit", "commit": upstream_pr, "pr": 133})
            self.assertEqual(report.errors + report.warnings, [])
            self.assertEqual(repo._git("rev-parse", "refs/proof/pr-133"), upstream_pr)

    def test_a_fork_entry_resolves_through_the_forks_pull_ref(self):
        with GitRepo() as repo:
            _, fork_pr = self.upstream_and_fork(repo)
            bad = {"kind": "pr-commit", "commit": fork_pr, "pr": 133, "repository": FORK}
            self.assertFalse(provenance.has_commit(fork_pr))  # only the fetch brings it
            report = self.check(bad)
            self.assertEqual(report.errors + report.warnings, [])
            self.assertEqual(repo._git("rev-parse", f"refs/proof/repo/{FORK}/pr-133"), fork_pr)
            # The fork's ref never shadows upstream's PR of the same number.
            self.assertEqual(repo._git("for-each-ref", "refs/proof/pr-133"), "")

    def test_a_fork_commit_without_repository_is_rejected_with_the_fix_named(self):
        with GitRepo() as repo:
            _, fork_pr = self.upstream_and_fork(repo)
            report = self.check({"kind": "pr-commit", "commit": fork_pr, "pr": 133})
            self.assertTrue(any("not available in this clone after fetching PR #133 of the upstream" in error
                                and "knownBad.repository" in error for error in report.errors), report.errors)
            provenance.ensure_commit({"kind": "pr-commit", "commit": fork_pr, "pr": 133, "repository": FORK})
            report = self.check({"kind": "pr-commit", "commit": fork_pr, "pr": 133})  # now local, still not upstream's
            self.assertTrue(any("is not part of PR #133 (PR #133 of the upstream repository" in error
                                and "names the fork as knownBad.repository" in error for error in report.errors),
                            report.errors)

    def test_a_fork_entry_is_not_part_of_another_pr_of_the_fork(self):
        with GitRepo() as repo:
            upstream_pr, _ = self.upstream_and_fork(repo)
            report = self.check({"kind": "pr-commit", "commit": upstream_pr, "pr": 133, "repository": FORK})
            self.assertTrue(any(f"is not part of PR #133 (PR #133 of {FORK}" in error for error in report.errors),
                            report.errors)

    def test_an_unreachable_repository_is_a_warning_then_a_missing_commit(self):
        with GitRepo() as repo:
            repo.commit({"a.txt": "1"})
            repo._git("branch", "dev")
            report = self.check({"kind": "pr-commit", "commit": BAD_COMMIT, "pr": 5, "repository": "nobody/none"})
            self.assertTrue(any("could not fetch the head of PR #5 of nobody/none" in item for item in report.warnings))
            self.assertTrue(any("is not available in this clone" in error for error in report.errors))

    def test_pack_known_bad_fetches_a_fork_commit(self):
        with GitRepo() as repo:
            _, fork_pr = self.upstream_and_fork(repo)
            real, calls = subprocess.run, []

            def fake_build(command, **kwargs):  # git runs for real; the worktree and dotnet pack are skipped
                if command[0] == "dotnet" or command[1] == "worktree":
                    calls.append(command)
                    return subprocess.CompletedProcess(command, 0)
                return real(command, **kwargs)

            with patch.object(subprocess, "run", side_effect=fake_build), \
                    self.assertRaisesRegex(SystemExit, "expected one LiteDB package"):
                proof.pack_known_bad(fork_pr, str(self.host / "feed"), "133", FORK)
            self.assertTrue(provenance.has_commit(fork_pr))
            self.assertEqual(calls[0][:4] + calls[0][5:], ["git", "worktree", "add", "--detach", fork_pr])


class ForkShapeTests(unittest.TestCase):
    def test_repository_is_validated_offline(self):
        repro_files = {f"{FOLDER}/{REPRO}.csproj": csproj("0.0.0-knownbad.aaaaaaaaaaaa")}
        reason = "the defect was introduced and fixed inside the pull request"
        cases = {
            None: {"kind": "pr-commit", "commit": BAD_COMMIT, "pr": 133, "repository": FORK, "reason": reason},
            "must be a GitHub repository as 'owner/name'":
                {"kind": "pr-commit", "commit": BAD_COMMIT, "pr": 133, "repository": "https://x/y", "reason": reason},
            "must be a GitHub repository as 'owner/name', not 'a/..'":
                {"kind": "pr-commit", "commit": BAD_COMMIT, "pr": 133, "repository": "a/..", "reason": reason},
            "only qualifies a pr-commit": {"kind": "dev-commit", "commit": BAD_COMMIT, "repository": FORK,
                                           "reason": reason},
            "names its originating PR number": {"kind": "pr-commit", "commit": BAD_COMMIT, "pr": True,
                                                "repository": FORK, "reason": reason},
        }
        for expected, bad in cases.items():
            with self.subTest(expected), GitRepo() as repo:
                repo.commit({**BASE, **repro_files, LEDGER: ledger(entry(knownBad=bad))})
                code, output = run_quietly(proof.main, ["validate"])
                if expected is None:
                    self.assertEqual(code, 0, output)
                else:
                    self.assertEqual(code, 1, output)
                    self.assertIn(expected, output)

    def test_fork_known_bad_inputs_parse(self):
        with GitRepo() as repo:
            head = repo.commit(BASE)
            bad = proof.parse_known_bad(f"pr-commit:{head[:9]}@{FORK}#133", "reason")
            self.assertEqual(bad, {"kind": "pr-commit", "commit": head, "reason": "reason", "pr": 133,
                                   "repository": FORK})
            self.assertNotIn("repository", proof.parse_known_bad(f"pr-commit:{head}@133", "r"))
            for value in (f"pr-commit:{head}@{FORK}#x", f"pr-commit:{head}@bad repo#1"):
                with self.subTest(value), self.assertRaises(SystemExit):
                    proof.parse_known_bad(value, "reason")

    def test_the_matrix_carries_the_pr_and_repository(self):
        with GitRepo() as repo:
            repo.commit(BASE)
            tree = common.Tree("HEAD")
            bad = {"kind": "pr-commit", "commit": BAD_COMMIT, "pr": 133, "repository": FORK}
            item = proof.matrix_item(tree, entry(knownBad=bad))
            self.assertEqual((item["pr"], item["repository"]), (133, FORK))
            item = proof.matrix_item(tree, entry())
            self.assertEqual((item["pr"], item["repository"]), ("", ""))


def run_report(package_actual=0, latest_actual=1, version="5.0.20", swapped=False):
    def variant(expected, actual, project, reported):
        payload = {"useProjectReference": project, "liteDBPackageVersion": reported}
        output = [{"Stream": "stdout", "Text": json.dumps({"type": "configuration", "payload": payload})}]
        return {"Expected": expected, "Actual": actual, "Met": expected == actual,
                "UseProjectReference": project, "Output": output}
    package = variant(0, package_actual, swapped, version)
    latest = variant(1, latest_actual, not swapped, version)
    return {"Repros": [{"Id": REPRO, "State": 1, "Failed": package["Met"] is False or latest["Met"] is False,
                        "Package": package, "Latest": latest}]}


class VerifyTests(unittest.TestCase):
    def verify(self, data, version="5.0.20"):
        directory = tempfile.mkdtemp()
        try:
            path = os.path.join(directory, "proof.json")
            Path(path).write_text(json.dumps(data), encoding="utf-8")
            return run_quietly(proof.main, ["verify", "--report", path, "--repro", REPRO, "--expect-version", version])
        finally:
            shutil.rmtree(directory)

    def test_known_bad_failing_and_candidate_passing_is_a_proof(self):
        code, output = self.verify(run_report())
        self.assertEqual(code, 0, output)

    def test_anything_else_is_not_a_proof(self):
        cases = {
            "the known-bad state did not fail": run_report(package_actual=1),
            "the candidate did not pass": run_report(latest_actual=0),
            "not package 5.0.20": run_report(version="5.0.21"),
            "variants are swapped": run_report(swapped=True),
            "has no single result": {"Repros": []},
        }
        for expected, data in cases.items():
            with self.subTest(expected):
                code, output = self.verify(data)
                self.assertEqual(code, 1)
                self.assertIn(expected, output)


class VersionTests(unittest.TestCase):
    def test_versions_compare_in_nuget_normalized_form(self):
        cases = {"5.0.20": "5.0.20", "5.0.20.0": "5.0.20", "05.0.020": "5.0.20", "6.0.0-Prerelease.318": "6.0.0-prerelease.318",
                 "5.0.21+build.7": "5.0.21", "1.2.3.4": "1.2.3.4"}
        for given, expected in cases.items():
            with self.subTest(given):
                self.assertEqual(proof.normalize_version(given), expected)


class PackTests(unittest.TestCase):
    def test_reversion_rewrites_only_the_package_version(self):
        directory = Path(tempfile.mkdtemp())
        try:
            source, target = directory / "LiteDB.6.0.0-x.nupkg", directory / "LiteDB.0.0.0-knownbad.abc.nupkg"
            with zipfile.ZipFile(source, "w") as package:
                package.writestr("LiteDB.nuspec", "<package><metadata><id>LiteDB</id><version>6.0.0-x</version>"
                                                  "<dependencies><dependency version=\"1.0\"/></dependencies>"
                                                  "</metadata></package>")
                package.writestr("lib/net8.0/LiteDB.dll", b"binary")
            proof._reversion(source, "0.0.0-knownbad.abc", target)
            with zipfile.ZipFile(target) as package:
                nuspec = package.read("LiteDB.nuspec").decode()
                self.assertIn("<version>0.0.0-knownbad.abc</version>", nuspec)
                self.assertIn('<dependency version="1.0"/>', nuspec)
                self.assertEqual(package.read("lib/net8.0/LiteDB.dll"), b"binary")
        finally:
            shutil.rmtree(directory)


if __name__ == "__main__":
    unittest.main()
