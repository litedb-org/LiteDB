#!/usr/bin/env python3
"""Verify actual v10/v11 parents reject v11/v12 data and WAL without mutation."""
import argparse
import pathlib
import subprocess
import tempfile
from xml.sax.saxutils import escape


def main():
    root = pathlib.Path(__file__).resolve().parent.parent
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--v10-ref", default="f4c015b30a1ffcaa44f8d0e72ebe746c8a6e60f6")
    parser.add_argument("--v11-ref", default="bc2b91c3ae9ad39c83d5c404a770d37ed9ed0f5a")
    parser.add_argument("--v12-ref", default="3487038b0cc08156f694bb41db258ee34690b835")
    parser.add_argument("--max-version", type=int, choices=(11, 12), default=12,
                        help="Highest writer format to test (default: 12).")
    args = parser.parse_args()

    def run(*command):
        subprocess.run(command, cwd=root, check=True)

    with tempfile.TemporaryDirectory(prefix="litedb-parent-format-") as directory:
        temporary = pathlib.Path(directory)
        worktrees = []
        probes = {}
        try:
            for version, revision in ((10, args.v10_ref), (11, args.v11_ref), (12, args.v12_ref)):
                if version > args.max_version:
                    continue
                older = temporary / f"engine-v{version}"
                run("git", "worktree", "add", "--detach", str(older), revision)
                worktrees.append(older)
                run("dotnet", "build", str(older / "LiteDB/LiteDB.csproj"), "-c", "Release",
                    "-f", "net8.0", "-p:TestingEnabled=false", "--verbosity", "quiet")
                probe = temporary / f"probe-v{version}"
                probe.mkdir()
                assembly = escape(str(older / "LiteDB/bin/Release/net8.0/LiteDB.dll"))
                source = escape(str(root / "tools/FormatCompatibility/Program.cs"))
                (probe / "Probe.csproj").write_text(f'''<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework></PropertyGroup>
  <ItemGroup><Reference Include="LiteDB"><HintPath>{assembly}</HintPath></Reference>
    <Compile Include="{source}" /></ItemGroup>
</Project>\n''')
                run("dotnet", "build", str(probe / "Probe.csproj"), "-c", "Release",
                    "-p:TestingEnabled=false", "--verbosity", "quiet")
                probes[version] = str(probe / "bin/Release/net8.0/Probe.dll")

            for reader, writer in ((10, 11), (11, 12)):
                if writer > args.max_version:
                    continue
                fixtures = temporary / f"fixtures-v{writer}"
                fixtures.mkdir()
                for engine, mode in ((writer, "create"), (reader, "reject"), (writer, "verify")):
                    run("dotnet", probes[engine], mode, str(engine), str(writer), str(fixtures))
        finally:
            for worktree in reversed(worktrees):
                run("git", "worktree", "remove", "--force", str(worktree))


if __name__ == "__main__":
    main()
