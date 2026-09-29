#!/usr/bin/env python3
"""Check the committed 5.0.21 fixtures against the production assembly (TestingEnabled=false, no test hooks)."""
import argparse
import hashlib
import pathlib
import subprocess
import tempfile
import zipfile
from xml.sax.saxutils import escape

ARCHIVES = ("WalCrash_5_0_21", "DropIndex_5_0_21", "ForeignWal_5_0_21", "ConcurrentWalCrash_5_0_21",
            "DamagedDocument_5_0_21")


def digests(directory):
    return {path.relative_to(directory): hashlib.sha256(path.read_bytes()).hexdigest()
            for path in sorted(directory.rglob("*")) if path.is_file()}


def main():
    root = pathlib.Path(__file__).resolve().parent.parent
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--current-assembly", type=pathlib.Path,
                        help="Use an already-built production net8.0 assembly without building the current source.")
    parser.add_argument("--current-ref", default="HEAD", help="Revision to build, from a clean worktree (default HEAD).")
    parser.add_argument("--temp-root", type=pathlib.Path, help="Place the build, probe and fixture copies there.")
    args = parser.parse_args()

    def run(*command):
        subprocess.run([str(value) for value in command], cwd=root, check=True)

    with tempfile.TemporaryDirectory(prefix="litedb-5021-regressions-", dir=args.temp_root) as directory:
        temporary = pathlib.Path(directory).resolve()
        checkout = None
        try:
            if args.current_assembly:
                assembly = args.current_assembly.resolve()
            else:
                # Build a clean worktree of the revision: this checkout's hook-enabled outputs in
                # LiteDB/bin and LiteDB/obj are neither touched nor compiled in.
                checkout = temporary / "current"
                run("git", "worktree", "add", "--detach", checkout, args.current_ref)
                run("dotnet", "build", checkout / "LiteDB/LiteDB.csproj", "-c", "Release", "-f", "net8.0",
                    "-p:TestingEnabled=false", "--verbosity", "quiet")
                assembly = checkout / "LiteDB/bin/Release/net8.0/LiteDB.dll"
            print(f"current: assembly SHA-256 {hashlib.sha256(assembly.read_bytes()).hexdigest()} ({assembly})", flush=True)
            probe = temporary / "probe"
            probe.mkdir()
            source = escape(str(root / "tools/Regression5021Compatibility/Program.cs"))
            (probe / "Probe.csproj").write_text(f'''<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework></PropertyGroup>
  <ItemGroup><Reference Include="LiteDB"><HintPath>{escape(str(assembly))}</HintPath></Reference>
    <Compile Include="{source}" /></ItemGroup>
</Project>\n''')
            run("dotnet", "build", probe / "Probe.csproj", "-c", "Release", "--verbosity", "quiet")

            # The committed archives are only read; the probe copies each file it opens.
            fixtures = temporary / "fixtures"
            for archive in ARCHIVES:
                with zipfile.ZipFile(root / "LiteDB.Tests/Resources" / (archive + ".zip")) as entries:
                    entries.extractall(fixtures / archive)
            extracted = digests(fixtures)
            work = temporary / "work"
            work.mkdir()
            result = subprocess.run(["dotnet", str(probe / "bin/Release/net8.0/Probe.dll"), str(fixtures), str(work)],
                                    cwd=root)
            if digests(fixtures) != extracted:
                raise SystemExit("The probe changed an extracted fixture instead of a copy.")
            if result.returncode == 2:
                raise SystemExit("The probe refused the assembly: it is not a production build (see its output above).")
            if result.returncode:
                raise SystemExit(f"The production assembly mishandles the 5.0.21 fixtures (probe exit {result.returncode}).")
        finally:
            if checkout is not None:
                run("git", "worktree", "remove", "--force", checkout)


if __name__ == "__main__":
    main()
