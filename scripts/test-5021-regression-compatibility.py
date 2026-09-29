#!/usr/bin/env python3
"""Check the pinned 5.0.21 fixtures against the production assembly (TestingEnabled=false, no test hooks).

The archives live in litedb-org/LiteDB-Artifacts at the revision LiteDB.Tests/Resources/artifacts.json
pins, resolved like LiteDB.Tests/Utils/ArtifactFixtures.cs: $LITEDB_ARTIFACTS_DIR/<path> when set,
else a per-user cache, else a download; each is verified against its pinned SHA-256.
"""
import argparse
import hashlib
import json
import os
import pathlib
import subprocess
import tempfile
import urllib.request
import zipfile
from xml.sax.saxutils import escape

ARCHIVES = ("WalCrash_5_0_21", "ForeignWal_5_0_21", "ConcurrentWalCrash_5_0_21")
RAW_BASE_URL = "https://raw.githubusercontent.com/litedb-org/LiteDB-Artifacts/"


def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def artifact(root, name):
    """A local, hash-verified path of the pinned artifact (see ArtifactFixtures.Path)."""
    manifest = json.loads((root / "LiteDB.Tests/Resources/artifacts.json").read_text(encoding="utf-8"))
    entry = manifest["files"].get(name)
    if entry is None:
        raise SystemExit(f"{name} is not pinned in LiteDB.Tests/Resources/artifacts.json.")
    override = os.environ.get("LITEDB_ARTIFACTS_DIR")
    if override:
        path = pathlib.Path(override) / entry["path"]
        if not path.is_file():
            raise SystemExit(f"{name}: {path} does not exist (LITEDB_ARTIFACTS_DIR is set).")
    else:
        path = pathlib.Path(tempfile.gettempdir()) / "litedb-artifacts" / manifest["revision"] / entry["path"]
        if not path.is_file():
            path.parent.mkdir(parents=True, exist_ok=True)
            url = RAW_BASE_URL + manifest["revision"] + "/" + entry["path"]
            with urllib.request.urlopen(url, timeout=60) as response:
                data = response.read()
            if hashlib.sha256(data).hexdigest() != entry["sha256"]:
                raise SystemExit(f"{name}: {url} has SHA-256 {hashlib.sha256(data).hexdigest()}, pinned {entry['sha256']}.")
            partial = path.with_name(path.name + ".part")
            partial.write_bytes(data)
            partial.replace(path)
    actual = sha256(path)
    if actual != entry["sha256"]:
        raise SystemExit(f"{name}: {path} has SHA-256 {actual}, pinned {entry['sha256']}.")
    return path


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

            # The pinned archives are only read; the probe copies each file it opens.
            fixtures = temporary / "fixtures"
            for archive in ARCHIVES:
                with zipfile.ZipFile(artifact(root, archive + ".zip")) as entries:
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
