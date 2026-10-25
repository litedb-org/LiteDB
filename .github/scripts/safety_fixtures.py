"""Temporary git repositories for the safety-check unit tests."""
import ast
import contextlib
import io
import os
import shutil
import subprocess
import tempfile
from pathlib import Path
from unittest.mock import patch

import safety_common as common

ENV = {"GIT_AUTHOR_NAME": "test", "GIT_AUTHOR_EMAIL": "test@example.invalid",
       "GIT_COMMITTER_NAME": "test", "GIT_COMMITTER_EMAIL": "test@example.invalid"}


class GitRepo:
    """A scratch repository; use as a context manager so the checks resolve it as the root."""

    def __init__(self):
        self.path = Path(tempfile.mkdtemp(prefix="litedb-safety-"))
        self._git("init", "-q", "-b", "main")
        self._git("config", "core.autocrlf", "false")
        self._previous = None

    def _git(self, *args):
        return subprocess.check_output(["git", *args], cwd=self.path, env={**os.environ, **ENV}).decode().strip()

    def write(self, files):
        for name, content in files.items():
            target = self.path / name
            if content is None:
                if target.exists():
                    target.unlink()
                continue
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(content if isinstance(content, bytes) else content.encode("utf-8"))

    def commit(self, files, message="change"):
        self.write(files)
        self._git("add", "-A")
        self._git("commit", "-q", "--allow-empty", "-m", message)
        return self._git("rev-parse", "HEAD")

    def __enter__(self):
        self._previous = os.getcwd()
        os.chdir(self.path)
        common._ROOT.clear()
        return self

    def __exit__(self, *exc):
        os.chdir(self._previous)
        common._ROOT.clear()
        shutil.rmtree(self.path, ignore_errors=True)


def run_quietly(main, argv):
    """Run a check's main() without annotations; return (exit code, report output)."""
    output = io.StringIO()
    environment = {key: value for key, value in os.environ.items()
                   if key not in ("GITHUB_ACTIONS", "GITHUB_STEP_SUMMARY")}
    with patch.dict(os.environ, environment, clear=True), contextlib.redirect_stdout(output):
        code = main(argv)
    return code, output.getvalue()


def script_closure(module):
    """Repository paths of the .github/scripts modules that `module` imports, transitively, itself included."""
    scripts, seen, pending = Path(__file__).parent, set(), [module]
    while pending:
        name = pending.pop()
        if name in seen or not (scripts / f"{name}.py").is_file():
            continue
        seen.add(name)
        tree = ast.parse((scripts / f"{name}.py").read_text(encoding="utf-8"))
        pending += [alias.name for node in ast.walk(tree) if isinstance(node, ast.Import) for alias in node.names]
        pending += [node.module for node in ast.walk(tree) if isinstance(node, ast.ImportFrom) and node.module]
    return {f".github/scripts/{name}.py" for name in seen}


def csharp_class(name, methods, namespace="LiteDB.Tests.Engine"):
    """C# source for a test class; methods maps name -> (attribute, body)."""
    members = "\n".join(f"        [{attribute}]\n        public void {method}()\n        {{\n            {body}\n        }}\n"
                        for method, (attribute, body) in methods.items())
    return f"namespace {namespace}\n{{\n    public class {name}\n    {{\n{members}    }}\n}}\n"
