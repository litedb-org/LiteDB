#!/usr/bin/env python3
"""Check v13 rejection with the actual v12 parent in a separate process."""
import argparse
import pathlib
import subprocess
import tempfile
from xml.sax.saxutils import escape

COMMAND_TIMEOUT_SECONDS = 900

root = pathlib.Path(__file__).resolve().parent.parent
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--parent-ref", default="2b7a4ddc2f210f97d1fad9991cb8e0317c0f4e93")
args = parser.parse_args()


def run(*command, check=True, stderr=None):
    try:
        return subprocess.run(command, cwd=root, check=check, stderr=stderr, timeout=COMMAND_TIMEOUT_SECONDS)
    except subprocess.TimeoutExpired:
        raise SystemExit(f"Timed out after {COMMAND_TIMEOUT_SECONDS} s: {' '.join(command)}") from None


def require_commit(revision):
    # After squash merges or branch deletion a pinned parent may be reachable
    # only from pull-request refs, which clones do not fetch; request it by SHA.
    present = run("git", "cat-file", "-e", f"{revision}^{{commit}}", check=False, stderr=subprocess.DEVNULL)
    if present.returncode != 0:
        run("git", "fetch", "--no-tags", "origin", revision)


with tempfile.TemporaryDirectory(prefix="litedb-mvcc-compatibility-") as directory:
    temporary = pathlib.Path(directory)
    older = temporary / "parent"
    require_commit(args.parent_ref)
    run("git", "worktree", "add", "--detach", str(older), args.parent_ref)
    try:
        run("dotnet", "build", str(older / "LiteDB/LiteDB.csproj"), "-c", "Release",
            "-f", "net8.0", "-p:TestingEnabled=false")
        probe = temporary / "probe"
        probe.mkdir()
        assembly = escape(str(older / "LiteDB/bin/Release/net8.0/LiteDB.dll"))
        source = escape(str(root / "tools/VectorCompatibility/Previous/Program.cs"))
        (probe / "Previous.csproj").write_text(f'''<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework></PropertyGroup>
  <ItemGroup><Reference Include="LiteDB"><HintPath>{assembly}</HintPath></Reference>
    <Compile Include="{source}" /></ItemGroup>
</Project>\n''')
        current = str(root / "tools/VectorCompatibility/Current/Current.csproj")
        run("dotnet", "run", "--project", current, "-c", "Release",
            "-p:TestingEnabled=true", "--", "reclaim-create", directory)
        run("dotnet", "run", "--project", str(probe), "-c", "Release", "--", directory)
        run("dotnet", "run", "--project", current, "-c", "Release", "--no-build",
            "--", "reclaim-verify", directory)
    finally:
        run("git", "worktree", "remove", "--force", str(older))
