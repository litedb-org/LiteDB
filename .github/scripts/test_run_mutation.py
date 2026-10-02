import contextlib
import io
import json
import os
import shutil
import subprocess
import unittest
from pathlib import Path

import run_mutation as runner_module
import safety_common as common
from safety_fixtures import ENV, GitRepo, run_quietly

CONFIG = json.dumps({"stryker-config": {"project": "LiteDB.csproj", "target-framework": "net8.0"}})
TOOLS = json.dumps({"version": 1, "isRoot": True,
                    "tools": {"dotnet-stryker": {"version": "5.0.0", "commands": ["dotnet-stryker"]}}})
FILES = {runner_module.CONFIG: CONFIG, ".config/dotnet-tools.json": TOOLS, "LiteDB/A.cs": "class A { }\n"}


class Recorder:
    def __init__(self, codes=None):
        self.calls, self.codes = [], dict(codes or {})

    def __call__(self, command, cwd, env=None):
        self.calls.append((command, cwd, env, git_head(cwd)))  # git_head: the tree the command runs in
        return self.codes.get(command[1] if command[0] == "dotnet" else "gate", 0)


def git_head(cwd):
    return subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=cwd).decode().strip()


class RunMutationTests(unittest.TestCase):
    def setUp(self):
        self.output = Path(os.environ.get("TMPDIR", "/tmp")) / f"run-mutation-test-{os.getpid()}"

    def tearDown(self):
        shutil.rmtree(self.output, ignore_errors=True)

    def run_main(self, args, recorder):
        with contextlib.redirect_stderr(io.StringIO()):
            return run_quietly(lambda argv: runner_module.main(argv, runner=recorder), args)

    def test_regular_checkout_runs_in_place_with_pinned_config_and_environment(self):
        with GitRepo() as repo:
            base = repo.commit(FILES)
            head = repo.commit({"LiteDB/A.cs": "class A { int x; }\n"})
            recorder = Recorder()
            code, output = self.run_main(["--base", base[:7], "--output", str(self.output)], recorder)
            self.assertEqual(code, 0, output)
            self.assertFalse(runner_module.is_linked_worktree(str(repo.path)))
            stryker = next(call for call in recorder.calls if call[0][:2] == ["dotnet", "stryker"])
            command, cwd, env, tree_head = stryker
            self.assertEqual(Path(cwd).resolve(), (repo.path / "LiteDB.Tests").resolve())
            self.assertIn(f"--since:{base}", command)  # full SHA, never the short form given
            self.assertEqual(Path(command[command.index("--config-file") + 1]).resolve(),
                             (repo.path / runner_module.CONFIG).resolve())
            self.assertEqual((env["TestingEnabled"], env["TargetFramework"]), ("true", "net8.0"))
            self.assertEqual(tree_head, head)
            gate = recorder.calls[-1][0]
            self.assertTrue(gate[1].endswith("mutation_gate.py"))
            self.assertEqual(gate[gate.index("--base") + 1], base)
            self.assertIn("dotnet-stryker 5.0.0", output)

    def test_linked_worktree_runs_in_a_temporary_clone_of_its_head(self):
        with GitRepo() as repo:
            base = repo.commit(FILES)
            worktree = repo.path.parent / f"{repo.path.name}-wt"
            subprocess.check_call(["git", "worktree", "add", "-q", "--detach", str(worktree)], cwd=repo.path)
            try:
                (worktree / "LiteDB/A.cs").write_text("class A { int y; }\n", encoding="utf-8")
                env = {**os.environ, **ENV}
                subprocess.check_call(["git", "commit", "-qam", "detached change"], cwd=worktree, env=env)
                head = git_head(worktree)
                os.chdir(worktree)
                common._ROOT.clear()
                self.assertTrue(runner_module.is_linked_worktree(str(worktree)))
                recorder = Recorder()
                code, output = self.run_main(["--base", base, "--output", str(self.output),
                                              "--test-case-filter", "FullyQualifiedName~Lock", "--advisory"],
                                             recorder)
                self.assertEqual(code, 0, output)
                command, cwd, _, tree_head = next(call for call in recorder.calls if call[0][:2] == ["dotnet", "stryker"])
                self.assertEqual(tree_head, head)  # the detached worktree commit, in the clone
                self.assertNotEqual(Path(cwd).resolve(), (worktree / "LiteDB.Tests").resolve())
                narrowed = json.loads(Path(command[command.index("--config-file") + 1]).read_text(encoding="utf-8"))
                self.assertEqual(narrowed["stryker-config"]["test-case-filter"], "FullyQualifiedName~Lock")
                self.assertEqual(recorder.calls[-1][0][-1], "--advisory")
                self.assertFalse(Path(cwd).exists(), "the temporary clone is removed")
            finally:
                os.chdir(repo.path)
                subprocess.call(["git", "worktree", "remove", "--force", str(worktree)], cwd=repo.path)

    def test_non_csharp_test_project_changes_do_not_disable_since(self):
        with GitRepo() as repo:
            base = repo.commit({**FILES, "LiteDB.Tests/Data/fixture.json": "{}\n", "LiteDB.Tests/Gone.txt": "x\n",
                                "LiteDB.Tests/T.cs": "class T { }\n"})
            repo.commit({"LiteDB/A.cs": "class A { int x; }\n", "LiteDB/A.json": "{}\n",
                         "LiteDB.Tests/Data/fixture.json": "{\"changed\": 1}\n", "LiteDB.Tests/Gone.txt": None,
                         "LiteDB.Tests/LiteDB.Tests.csproj": "<Project />\n", "LiteDB.Tests/T.cs": "class T { int y; }\n"})
            (repo.path / "LiteDB.Tests/untracked.txt").write_text("u\n", encoding="utf-8")
            recorder = Recorder()
            code, output = self.run_main(["--base", base, "--output", str(self.output)], recorder)
            self.assertEqual(code, 0, output)
            command = next(call[0] for call in recorder.calls if call[0][:2] == ["dotnet", "stryker"])
            config = json.loads(Path(command[command.index("--config-file") + 1]).read_text(encoding="utf-8"))
            ignored = config["stryker-config"]["since"]["ignore-changes-in"]
            self.assertEqual(ignored, ["**/LiteDB.Tests/Data/fixture.json", "**/LiteDB.Tests/Gone.txt",
                                       "**/LiteDB.Tests/LiteDB.Tests.csproj", "**/LiteDB.Tests/untracked.txt"])
            self.assertNotIn("test-case-filter", config["stryker-config"])
            self.assertIn("4 changed non-C# file(s) under LiteDB.Tests", output)

    def test_committed_ignore_patterns_are_kept_and_glob_characters_neutralised(self):
        committed = json.dumps({"stryker-config": {"since": {"ignore-changes-in": ["**/*.md"]}}})
        with GitRepo() as repo:
            base = repo.commit({**FILES, runner_module.CONFIG: committed})
            repo.commit({"LiteDB.Tests/Data/[a]*b?.bin": "x\n"})
            recorder = Recorder()
            code, output = self.run_main(["--base", base, "--output", str(self.output),
                                          "--test-case-filter", "Name~X"], recorder)
            self.assertEqual(code, 0, output)
            command = next(call[0] for call in recorder.calls if call[0][:2] == ["dotnet", "stryker"])
            config = json.loads(Path(command[command.index("--config-file") + 1]).read_text(encoding="utf-8"))
            self.assertEqual(config["stryker-config"]["since"]["ignore-changes-in"],
                             ["**/*.md", "**/LiteDB.Tests/Data/?a??b?.bin"])
            self.assertEqual(config["stryker-config"]["test-case-filter"], "Name~X")

    def test_stryker_failure_skips_the_gate(self):
        with GitRepo() as repo:
            base = repo.commit(FILES)
            recorder = Recorder({"stryker": 3})
            code, _ = self.run_main(["--base", base, "--output", str(self.output)], recorder)
            self.assertEqual(code, 3)
            self.assertFalse(any(call[0][1].endswith("mutation_gate.py") for call in recorder.calls))


if __name__ == "__main__":
    unittest.main()
