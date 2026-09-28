#!/usr/bin/env python3
"""Files written by published LiteDB packages open correctly or are refused unchanged.

Builds tools/PrereleaseCompatibility against the newest published prereleases and
the latest stable release, writes plain/encrypted and clean/dirty-WAL fixtures with
each, then opens every fixture with the current source. A misread, a refusal that
changed a byte, or a crash fails; reading, migrating or refusing cleanly passes.
See docs/rules/compatibility.md#published-prereleases.
"""
import argparse
import json
import os
import pathlib
import re
import subprocess
import tempfile
import urllib.request
from xml.sax.saxutils import escape

ROOT = pathlib.Path(__file__).resolve().parent.parent
PROBE = ROOT / "tools" / "PrereleaseCompatibility" / "Program.cs"
NUGET_INDEX = "https://api.nuget.org/v3-flatcontainer/litedb/index.json"
TIMEOUT_SECONDS = 900


def run(*command, capture=False):
    try:
        result = subprocess.run(command, cwd=ROOT, check=True, timeout=TIMEOUT_SECONDS, text=True,
                                stdout=subprocess.PIPE if capture else None)
    except subprocess.TimeoutExpired:
        raise SystemExit(f"Timed out after {TIMEOUT_SECONDS} s: {' '.join(command)}") from None
    return result.stdout if capture else ""


def versions_to_check(count):
    with urllib.request.urlopen(NUGET_INDEX, timeout=30) as response:
        versions = json.load(response)["versions"]
    prereleases = [version for version in versions if "-" in version][-count:]
    stable = [version for version in versions if "-" not in version][-1:]
    return stable + prereleases


def build_probe(directory, reference):
    directory.mkdir(parents=True)
    (directory / "Probe.csproj").write_text(f"""<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>disable</ImplicitUsings><Nullable>disable</Nullable></PropertyGroup>
  <ItemGroup>{reference}<Compile Include="{escape(str(PROBE))}" /></ItemGroup>
</Project>
""")
    run("dotnet", "build", str(directory / "Probe.csproj"), "-c", "Release", "-p:TestingEnabled=false",
        "-p:GitVersionEnabled=false", "--verbosity", "quiet", "-o", str(directory.parent / (directory.name + "-bin")))
    return str(directory.parent / (directory.name + "-bin") / "Probe.dll")


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--prereleases", type=int, default=5, help="How many of the newest prereleases to check")
    parser.add_argument("--versions", nargs="*", help="Explicit package versions instead of the newest ones")
    args = parser.parse_args()
    versions = args.versions or versions_to_check(args.prereleases)
    rows = []
    with tempfile.TemporaryDirectory(prefix="litedb-prerelease-compat-") as scratch:
        scratch = pathlib.Path(scratch)
        current = build_probe(scratch / "current", '<ProjectReference Include="{}" />'.format(
            escape(str(ROOT / "LiteDB" / "LiteDB.csproj"))))
        for version in versions:
            probe = build_probe(scratch / f"writer-{version}", f'<PackageReference Include="LiteDB" Version="{version}" />')
            fixtures = scratch / f"fixtures-{version}"
            fixtures.mkdir()
            written = run("dotnet", probe, "create", str(fixtures), capture=True)
            loaded = re.search(r"LiteDB loaded: (\S+)", written)
            if not loaded or not loaded.group(1).split("+")[0].lower() == version.lower():
                raise SystemExit(f"The {version} writer loaded {loaded.group(1) if loaded else 'an unknown LiteDB'}")
            stable = ["must-open"] if "-" not in version else []  # stable files must open (policy)
            outcome = run("dotnet", current, "check", str(fixtures), *stable, capture=True)
            print(f"== LiteDB {version}\n{outcome}")
            rows += [f"| {version} | {line.split(': ', 1)[0]} | {line.split(': ', 1)[1]} |"
                     for line in outcome.splitlines() if not line.startswith("LiteDB loaded")]
    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    table = "\n".join(["| Written by | Fixture | Current engine |", "| --- | --- | --- |"] + rows)
    if summary:
        with open(summary, "a", encoding="utf-8") as handle:
            handle.write(f"## Published-package compatibility\n\n{table}\n")
    print(table)


if __name__ == "__main__":
    main()
