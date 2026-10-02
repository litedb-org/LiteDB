"""Run Stryker.NET on the diff since a merge-base, then list survivors with mutation_gate.py.

One entry point for CI (.github/workflows/mutation.yml) and local runs, so both use
the pinned tool (.config/dotnet-tools.json), the committed config
(LiteDB.Tests/stryker-config.json, passed with --config-file) and the same
environment:

- TestingEnabled=true: MSBuild reads environment variables as properties, so the
  LiteDB that Stryker compiles and mutates has the TESTING hooks the tests call.
- TargetFramework=net8.0: makes both LiteDB and LiteDB.Tests single-target builds
  (Stryker's own `dotnet build` passes no framework), matching the config's
  target-framework.
- --since:<full merge-base SHA>: Stryker resolves short SHAs unreliably.

Stryker's git library resolves a linked `git worktree` to the main checkout and
then diffs the wrong working tree. From a linked worktree the script therefore
clones the worktree's HEAD into a temporary regular clone (sharing its objects)
and runs there; uncommitted changes are not part of that run.

How --since picks mutants (Stryker 5 SinceMutantFilter/GitDiffProvider): a
changed file under the test project directory (the directory Stryker runs in,
LiteDB.Tests/) is a "test file", any other changed file a "source file". A
changed C# source file re-tests all of its mutants; a changed C# test file
re-tests every mutant its tests cover (mutation_gate.py still reports only
mutants on changed lines). A changed test file that does not end in ".cs"
(a .json fixture, the .csproj, this config) re-tests every mutant in the
whole mutate scope, because Stryker cannot tell which mutants it affects.
The gate lists only mutants on changed lines, and every mutant of a changed
source file is tested anyway, so that full-scope run buys nothing here. The
helper therefore adds each changed non-C# file under LiteDB.Tests/ (tracked
changes since the base, deletions included, and untracked files) to the
config's since.ignore-changes-in; only C# changes decide the mutant set.
Changed non-C# files elsewhere are source files that match no mutated file,
so they never widened the set.
"""
import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

import safety_common as common

CONFIG = "LiteDB.Tests/stryker-config.json"
TEST_PROJECT_DIR = "LiteDB.Tests"
TOOL = "dotnet-stryker"
FRAMEWORK = "net8.0"


def is_linked_worktree(cwd):
    """True when cwd is a linked worktree (its git dir is not the common git dir)."""
    git_dir = common.git("rev-parse", "--absolute-git-dir", cwd=cwd).strip()
    common_dir = common.git("rev-parse", "--git-common-dir", cwd=cwd).strip()
    return Path(git_dir).resolve() != (Path(cwd) / common_dir).resolve()


def prepare_tree(root, scratch):
    """Return (directory to run in, clone path or None)."""
    if not is_linked_worktree(root):
        return root, None
    head = common.git("rev-parse", "HEAD", cwd=root).strip()
    if common.git("status", "--porcelain", "--untracked-files=no", cwd=root).strip():
        print(f"warning: {root} has uncommitted changes; mutating the committed HEAD {head} only", file=sys.stderr)
    clone = Path(scratch) / "tree"
    subprocess.check_call(["git", "clone", "--quiet", "--shared", "--no-checkout", str(root), str(clone)])
    subprocess.check_call(["git", "-c", "advice.detachedHead=false", "checkout", "--quiet", "--detach", head],
                          cwd=clone)
    print(f"Linked worktree: running in a temporary clone {clone} of {head}")
    return str(clone), str(clone)


def non_csharp_test_changes(tree, base):
    """Files under TEST_PROJECT_DIR not ending in ".cs" that Stryker's diff (base tree vs working tree) sees."""
    changed = common.git("diff", "--name-only", "--no-renames", base, "--", TEST_PROJECT_DIR, cwd=tree).splitlines()
    untracked = common.git("ls-files", "--others", "--exclude-standard", "--", TEST_PROJECT_DIR, cwd=tree).splitlines()
    return sorted({path for path in changed + untracked if path and not path.endswith(".cs")})


def ignore_pattern(path):
    """A since.ignore-changes-in glob matching the repository path; glob characters become '?'."""
    return "**/" + re.sub(r"[*?\[\]{}]", "?", path)


def stryker_config(tree, output, test_case_filter, ignored=()):
    """The committed config, or a copy that adds a test-case-filter (config-file only option) and
    since.ignore-changes-in entries for `ignored` paths."""
    config = Path(tree) / CONFIG
    if not test_case_filter and not ignored:
        return config
    data = json.loads(config.read_text(encoding="utf-8"))
    options = data["stryker-config"]
    if test_case_filter:
        options["test-case-filter"] = test_case_filter
    if ignored:
        since = options.setdefault("since", {})
        since["ignore-changes-in"] = list(since.get("ignore-changes-in", [])) + [ignore_pattern(p) for p in ignored]
    effective = Path(output) / "stryker-config.effective.json"
    effective.write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")
    return effective


def stryker_command(config, base, output, concurrency=None):
    command = ["dotnet", "stryker", "--config-file", str(config), f"--since:{base}",
               "--output", str(Path(output) / "stryker"), "--skip-version-check", "--log-to-file"]
    if concurrency:
        command += ["--concurrency", str(concurrency)]
    return command


def stryker_environment():
    return {**os.environ, "TestingEnabled": "true", "TargetFramework": FRAMEWORK}


def tool_version(tree):
    manifest = json.loads((Path(tree) / ".config/dotnet-tools.json").read_text(encoding="utf-8"))
    return manifest["tools"][TOOL]["version"]


def run(command, cwd, env=None):
    print("+ " + " ".join(command), flush=True)
    return subprocess.call(command, cwd=cwd, env=env)


def main(argv=None, runner=run):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--base", required=True, help="Merge-base revision; resolved to a full SHA")
    parser.add_argument("--output", required=True, help="Directory for the Stryker and gate reports")
    parser.add_argument("--test-case-filter", help="Narrow the tests (pilot/local runs only)")
    parser.add_argument("--concurrency", type=int, help="Override the config's concurrency")
    parser.add_argument("--keep-clone", action="store_true", help="Keep the temporary clone of a linked worktree")
    common.add_mode_arguments(parser)
    args = parser.parse_args(argv)
    root = common.repo_root()
    base = common.git("rev-parse", "--verify", f"{args.base}^{{commit}}", cwd=root).strip()
    head = common.git("rev-parse", "HEAD", cwd=root).strip()
    output = Path(args.output).resolve()
    output.mkdir(parents=True, exist_ok=True)
    scratch = tempfile.mkdtemp(prefix="litedb-mutation-")
    try:
        tree, clone = prepare_tree(root, scratch)
        ignored = non_csharp_test_changes(tree, base)
        if ignored:
            print(f"{len(ignored)} changed non-C# file(s) under {TEST_PROJECT_DIR}/ added to since.ignore-changes-in "
                  "(they would make --since re-test the whole mutate scope): " + ", ".join(ignored), flush=True)
        config = stryker_config(tree, output, args.test_case_filter, ignored)
        print(f"{TOOL} {tool_version(tree)} (pinned in .config/dotnet-tools.json); config {config}; "
              f"base {base}; head {head}; TestingEnabled=true TargetFramework={FRAMEWORK}", flush=True)
        code = runner(["dotnet", "tool", "restore"], tree)
        code = code or runner(["dotnet", "tool", "list", "--local"], tree)
        code = code or runner(["dotnet", "restore", f"{TEST_PROJECT_DIR}/LiteDB.Tests.csproj"], tree,
                              stryker_environment())
        code = code or runner(stryker_command(config, base, output, args.concurrency),
                              str(Path(tree) / TEST_PROJECT_DIR), stryker_environment())
        if code:
            print(f"Stryker failed (exit {code}); no survivor list", file=sys.stderr)
            return code
        report = output / "stryker" / "reports" / "mutation-report.json"
        gate = [sys.executable, str(Path(tree) / ".github/scripts/mutation_gate.py"), str(report),
                "--base", base, "--head", head,
                "--json", str(output / "mutation-gate.json"), "--markdown", str(output / "mutation-gate.md")]
        if args.blocking is not None:
            gate.append("--blocking" if args.blocking else "--advisory")
        return runner(gate, tree)
    finally:
        if args.keep_clone:
            print(f"Kept {scratch}")
        else:
            shutil.rmtree(scratch, ignore_errors=True)


if __name__ == "__main__":
    sys.exit(main())
