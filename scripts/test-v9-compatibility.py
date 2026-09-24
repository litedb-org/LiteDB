#!/usr/bin/env python3
"""Exercise genuine v9 vector files through v11 migration, v12 compact writes and v13 retirement."""
import argparse
import hashlib
import pathlib
import subprocess
import tempfile
from xml.sax.saxutils import escape


def main():
    root = pathlib.Path(__file__).resolve().parent.parent
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--v9-ref", default="0fd277aaed127b9dec99524277fb4173a2351167")
    parser.add_argument("--current-ref", default="HEAD")
    parser.add_argument("--current-assembly", type=pathlib.Path,
                        help="Use an already-built production net8.0 assembly without building the current source.")
    parser.add_argument("--temp-root", type=pathlib.Path, help="Place bounded fixtures/builds on a chosen filesystem.")
    args = parser.parse_args()

    def run(*command):
        subprocess.run([str(value) for value in command], cwd=root, check=True)

    with tempfile.TemporaryDirectory(prefix="litedb-v9-bridge-", dir=args.temp_root) as directory:
        temporary = pathlib.Path(directory)
        worktrees = []
        probes = {}
        try:
            for label, revision in (("v9", args.v9_ref), ("current", args.current_ref)):
                if label == "current" and args.current_assembly:
                    assembly = args.current_assembly.resolve()
                else:
                    checkout = temporary / label
                    run("git", "worktree", "add", "--detach", checkout, revision)
                    worktrees.append(checkout)
                    run("dotnet", "build", checkout / "LiteDB/LiteDB.csproj", "-c", "Release", "-f", "net8.0",
                        "-p:TestingEnabled=false", "--verbosity", "quiet")
                    assembly = checkout / "LiteDB/bin/Release/net8.0/LiteDB.dll"
                print(f"{label}: assembly SHA-256 {hashlib.sha256(assembly.read_bytes()).hexdigest()}", flush=True)
                probe = temporary / ("probe-" + label)
                probe.mkdir()
                defines = "<DefineConstants>CURRENT</DefineConstants>" if label == "current" else ""
                source = escape(str(root / "tools/V9Compatibility/Program.cs"))
                (probe / "Probe.csproj").write_text(f'''<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework>{defines}</PropertyGroup>
  <ItemGroup><Reference Include="LiteDB"><HintPath>{escape(str(assembly))}</HintPath></Reference>
    <Compile Include="{source}" /></ItemGroup>
</Project>\n''')
                run("dotnet", "build", probe / "Probe.csproj", "-c", "Release", "--verbosity", "quiet")
                probes[label] = probe / "bin/Release/net8.0/Probe.dll"
            fixtures = temporary / "fixtures"
            fixtures.mkdir()
            for label, mode in (("v9", "create"), ("current", "upgrade"), ("v9", "reject"), ("current", "verify")):
                run("dotnet", probes[label], mode, fixtures)
        finally:
            for checkout in reversed(worktrees):
                run("git", "worktree", "remove", "--force", checkout)


if __name__ == "__main__":
    main()
