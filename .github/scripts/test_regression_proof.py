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
                ({"kind": "pr-commit", "commit": on_dev, "pr": 7}, "exact PR membership requires online"),
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


    def test_unresolved_dev_ref_refuses_unknown_classification(self):
        with GitRepo() as repo:
            commit = repo.commit({"base.txt": "base"})
            repo._git("update-ref", "refs/pull/7/head", commit)
            repo._git("update-ref", "refs/proof/pr-7", commit)
            repo._git("remote", "add", "origin", str(repo.path))
            for kind in ("dev-commit", "pr-commit"):
                for offline in (False, True):
                    with self.subTest(kind=kind, offline=offline):
                        report = common.Report("")
                        with patch.object(provenance, "listed_pr_commit") as membership:
                            provenance.check_commit({"kind": kind, "commit": commit, "pr": 7},
                                                    REPRO, report, "origin/missing-dev", offline=offline)
                        self.assertTrue(any("cannot determine ancestry" in error for error in report.errors),
                                        report.errors)
                        membership.assert_not_called()

    def test_originating_pr_pin_survives_each_merge_method(self):
        for method in ("merge", "squash", "rebase"):
            with self.subTest(method=method), GitRepo() as repo:
                repo.commit({"base.txt": "base"})
                repo._git("branch", "dev")
                repo._git("checkout", "-q", "-b", "feature")
                bad = repo.commit({"bug.txt": "bad"})
                fixed = repo.commit({"bug.txt": "fixed"})
                repo._git("update-ref", "refs/pull/7/head", fixed)
                repo._git("remote", "add", "origin", str(repo.path))
                repo._git("checkout", "-q", "dev")
                repo.commit({"unrelated.txt": "dev advanced"})
                if method == "merge":
                    repo._git("merge", "-q", "--no-ff", "feature", "-m", "merge")
                elif method == "squash":
                    repo._git("merge", "-q", "--squash", "feature")
                    repo.commit({}, "squash")
                else:
                    repo._git("cherry-pick", bad, fixed)  # replay with new parents, as rebase merge does
                report = common.Report("")
                with patch.object(provenance, "listed_pr_commit", return_value=True, create=True) as membership:
                    proof.check_provenance(entry(knownBad={"kind": "pr-commit", "commit": bad, "pr": 7}),
                                           report, "dev")
                self.assertEqual(report.errors, [])
                self.assertEqual(membership.call_count, int(method == "merge"))

    def test_on_dev_exception_rejects_inherited_history_wrong_pr_and_api_errors(self):
        with GitRepo() as repo:
            inherited = repo.commit({"base.txt": "base"})
            repo._git("branch", "dev")
            repo._git("checkout", "-q", "-b", "feature")
            bad = repo.commit({"bug.txt": "bad"})
            fixed = repo.commit({"bug.txt": "fixed"})
            repo._git("update-ref", "refs/pull/7/head", fixed)
            repo._git("update-ref", "refs/pull/8/head", inherited)
            repo._git("remote", "add", "origin", str(repo.path))
            repo._git("checkout", "-q", "dev")
            unrelated = repo.commit({"other.txt": "other"})
            repo._git("merge", "-q", "--no-ff", "feature", "-m", "merge")
            for commit, pr, result, error, calls in (
                    (inherited, 7, False, "inherited dev history", 1),
                    (unrelated, 7, True, "is not part of PR #7", 0),
                    (bad, 8, True, "is not part of PR #8", 0),
                    (bad, 7, OSError("API unavailable"), "could not verify exact commit membership", 1)):
                with self.subTest(commit=commit, pr=pr, result=result):
                    report = common.Report("")
                    with patch.object(provenance, "listed_pr_commit", create=True,
                                      **({"side_effect": result} if isinstance(result, Exception)
                                         else {"return_value": result})) as membership:
                        proof.check_provenance(entry(knownBad={"kind": "pr-commit", "commit": commit, "pr": pr}),
                                               report, "dev")
                    self.assertTrue(any(error in item for item in report.errors), report.errors)
                    self.assertEqual(membership.call_count, calls)

    def test_exact_membership_uses_the_originating_repository_and_paginates(self):
        for remote in ("https://github.com/upstream/LiteDB", "https://github.com/upstream/LiteDB.git",
                       "git@github.com:upstream/LiteDB.git", "ssh://git@github.com/upstream/LiteDB.git"):
            for repository in (None, "fork/LiteDB"):
                with self.subTest(remote=remote, repository=repository), GitRepo() as repo:
                    repo._git("remote", "add", "origin", remote)
                    bad = {"kind": "pr-commit", "commit": BAD_COMMIT, "pr": 7}
                    if repository:
                        bad["repository"] = repository
                    pages = [io.BytesIO(json.dumps([{"sha": "b" * 40}] * 100).encode()),
                             io.BytesIO(json.dumps([{"sha": BAD_COMMIT}]).encode())]
                    with patch.object(provenance.urllib.request, "urlopen", side_effect=pages) as request:
                        self.assertTrue(provenance.listed_pr_commit(bad))
                    urls = [call.args[0].full_url for call in request.call_args_list]
                    prefix = f"https://api.github.com/repos/{repository or 'upstream/LiteDB'}/pulls/7/commits"
                    self.assertEqual(urls, [f"{prefix}?per_page=100&page={page}" for page in (1, 2)])

    def test_exact_membership_fails_closed_on_absence_limits_and_malformed_responses(self):
        bad = {"commit": BAD_COMMIT, "pr": 7, "repository": "fork/LiteDB"}
        for payloads in ([[]], [[{"sha": "b" * 40}] * 100] * 3):
            with self.subTest(pages=len(payloads)):
                pages = [io.BytesIO(json.dumps(payload).encode()) for payload in payloads]
                with patch.object(provenance.urllib.request, "urlopen", side_effect=pages) as request:
                    self.assertFalse(provenance.listed_pr_commit(bad))
                self.assertEqual(request.call_count, len(payloads))
        for payload in ({"message": "error"}, [None], [{"sha": "invalid"}]):
            with self.subTest(payload=payload), \
                    patch.object(provenance.urllib.request, "urlopen", return_value=io.BytesIO(json.dumps(payload).encode())):
                with self.assertRaises(ValueError):
                    provenance.listed_pr_commit(bad)


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

    def test_an_unreachable_repository_is_an_error(self):
        with GitRepo() as repo:
            repo.commit({"a.txt": "1"})
            repo._git("branch", "dev")
            report = self.check({"kind": "pr-commit", "commit": BAD_COMMIT, "pr": 5, "repository": "nobody/none"})
            self.assertTrue(any("could not fetch the head of PR #5 of nobody/none" in item for item in report.errors))
            self.assertTrue(any("is not available in this clone" in error for error in report.errors))

    def test_a_stale_proof_ref_never_answers_for_a_pr_that_cannot_be_fetched(self):
        """A ref left by an earlier fetch (here: the fork's PR head under upstream's name) is not evidence."""
        with GitRepo() as repo:
            _, fork_pr = self.upstream_and_fork(repo)
            provenance.ensure_commit({"kind": "pr-commit", "commit": fork_pr, "pr": 133, "repository": FORK})
            repo._git("update-ref", "refs/pull/133/head", "-d")  # upstream has no PR #133
            repo._git("update-ref", "refs/proof/pr-133", fork_pr)  # stale, from another repository
            report = self.check({"kind": "pr-commit", "commit": fork_pr, "pr": 133})
            self.assertTrue(any("could not fetch the head of PR #133 of the upstream" in error
                                and "knownBad.repository" in error for error in report.errors), report.errors)
            self.assertTrue(any("is not part of PR #133" in error for error in report.errors), report.errors)
            self.assertEqual(repo._git("for-each-ref", "refs/proof/pr-133"), "")  # the stale ref is gone

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
    def test_missing_exact_package_fails_closed(self):
        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaisesRegex(SystemExit, "missing exact LiteDB package 0.0.0-knownbad.abc"):
                proof.require_exact_package(directory, "0.0.0-knownbad.abc")

    def test_pack_matrix_skips_published_packages_and_checks_each_commit_package(self):
        entries = [entry(knownBad={"kind": "package", "version": "5.0.20"}),
                   entry(repro="Issue_2", knownBad={"kind": "pr-commit", "commit": BAD_COMMIT, "pr": 3137})]
        with tempfile.TemporaryDirectory() as directory, \
                patch.object(proof, "load", return_value=entries), \
                patch.object(proof, "pack_known_bad", return_value="0.0.0-knownbad.aaaaaaaaaaaa") as pack, \
                patch.object(proof, "require_exact_package") as require:
            versions = proof.pack_known_bad_matrix(directory, tree=object())
        self.assertEqual(versions, ["0.0.0-knownbad.aaaaaaaaaaaa"])
        pack.assert_called_once_with(BAD_COMMIT, directory, 3137, None)
        require.assert_called_once_with(directory, "0.0.0-knownbad.aaaaaaaaaaaa")

    def test_cli_rejects_empty_arguments_before_fetch_or_build(self):
        for arguments, error in ((["--pr", ""], "--pr must be"),
                                 (["--pr", "1", "--repository", ""], "--repository must be")):
            with self.subTest(arguments=arguments), \
                    patch.object(proof.common, "repo_root", return_value=Path.cwd()), \
                    patch.object(proof.provenance, "ensure_commit") as fetch, \
                    patch.object(proof.subprocess, "run", side_effect=AssertionError("fetch/build must not run")) as run:
                with self.assertRaisesRegex(SystemExit, error):
                    proof.main(["pack-known-bad", "--commit", BAD_COMMIT, "--feed", "unused", *arguments])
                fetch.assert_not_called()
                run.assert_not_called()

    def test_cli_packs_with_omitted_upstream_and_fork_arguments(self):
        for arguments, source in (([], None),
                                  (["--pr", "1"], {"kind": "pr-commit", "commit": BAD_COMMIT, "pr": 1}),
                                  (["--pr", "1", "--repository", FORK],
                                   {"kind": "pr-commit", "commit": BAD_COMMIT, "pr": 1, "repository": FORK})):
            with self.subTest(arguments=arguments), tempfile.TemporaryDirectory() as directory:
                def fake_build(command, **kwargs):
                    if command[0] == "dotnet":
                        packed = Path(command[command.index("-o") + 1])
                        packed.mkdir(parents=True)
                        with zipfile.ZipFile(packed / "LiteDB.6.0.0.nupkg", "w") as package:
                            package.writestr("LiteDB.nuspec", "<package><metadata><version>6.0.0</version>"
                                                              "</metadata></package>")
                    return subprocess.CompletedProcess(command, 0)

                feed = Path(directory) / "feed"
                with patch.object(proof.provenance, "ensure_commit", return_value=True) as fetch, \
                        patch.object(proof.common, "repo_root", return_value=Path(directory)), \
                        patch.object(proof.tempfile, "mkdtemp", return_value=str(Path(directory) / "scratch")), \
                        patch.object(proof.subprocess, "run", side_effect=fake_build):
                    code, output = run_quietly(proof.main, ["pack-known-bad", "--commit", BAD_COMMIT,
                                                          "--feed", str(feed), *arguments])
                self.assertEqual(code, 0, output)
                self.assertIn("0.0.0-knownbad.aaaaaaaaaaaa", output)
                self.assertTrue((feed / "LiteDB.0.0.0-knownbad.aaaaaaaaaaaa.nupkg").is_file())
                if source is None:
                    fetch.assert_not_called()
                else:
                    fetch.assert_called_once_with(source)

    def test_pack_rejects_invalid_arguments_before_fetch_or_build(self):
        cases = [("abc", None), ("0", None), ("-1", None), (True, None), ("", None),
                 ("1", "../repo"), ("1", "owner/repo..bad"), ("1", ""),
                 (None, "owner/repo")]
        for pr, repository in cases:
            with self.subTest(pr=pr, repository=repository), \
                    patch.object(proof.provenance, "ensure_commit") as fetch, \
                    patch.object(proof.subprocess, "run") as run:
                with self.assertRaisesRegex(SystemExit, "--pr|--repository"):
                    proof.pack_known_bad("a" * 40, "unused", pr, repository)
                fetch.assert_not_called()
                run.assert_not_called()

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
