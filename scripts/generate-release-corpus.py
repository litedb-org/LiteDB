#!/usr/bin/env python3
"""Generate immutable, real released-writer files; run only when updating the corpus."""
import hashlib
import json
import pathlib
import subprocess
import sys
import tempfile
import urllib.error
import urllib.request
import zipfile

def run_operation(command, description, timeout):
    try:
        subprocess.run(command, check=True, timeout=timeout)
    except subprocess.TimeoutExpired as error:
        raise SystemExit(f"Timed out after {timeout}s: {description}") from error


root = pathlib.Path(__file__).resolve().parent.parent
output = pathlib.Path(sys.argv[1]).resolve()
output.mkdir(parents=True, exist_ok=True)
try:
    with urllib.request.urlopen("https://api.nuget.org/v3-flatcontainer/litedb/index.json", timeout=60) as response:
        versions = [v for v in json.load(response)["versions"] if v.startswith(("4.", "5.")) and "-" not in v]
except (TimeoutError, urllib.error.URLError) as error:
    raise SystemExit(f"Failed to fetch released LiteDB version list: {error}") from error
runtime = "mcr.microsoft.com/dotnet/runtime@sha256:96d655c489cdf341f7679510efe51a5ba4e174d74aaaf613f7578f2e25c3717d"
run_operation(["docker", "pull", runtime], f"pull writer runtime {runtime}", 300)
manifest = {"schema": 1, "source": "https://api.nuget.org/v3-flatcontainer/litedb/index.json",
    "writer_runtime": runtime, "generator_sha256": hashlib.sha256(pathlib.Path(__file__).read_bytes()).hexdigest(),
    "corpus_sha256": hashlib.sha256((root / "tools/ReleaseCompatibility/Corpus.cs").read_bytes()).hexdigest(), "fixtures": []}
for version in versions:
    project = root / "tools/ReleaseCompatibility/Writer/Writer.csproj"
    run_operation(["dotnet", "build", str(project), "-c", "Release", "-p:ReleaseVersion=" + version],
        f"build writer for LiteDB {version}", 600)
    package = pathlib.Path.home() / ".nuget/packages/litedb" / version / ("litedb." + version + ".nupkg")
    for variant in ("plain", "encrypted"):
        name = "litedb-" + version + "-" + variant
        with tempfile.TemporaryDirectory() as directory:
            database = pathlib.Path(directory) / (name + ".db")
            binary = project.parent / "bin/Release/netcoreapp3.1"
            run_operation(["docker", "run", "--rm", "--network", "none",
                "-v", str(binary) + ":/app:ro", "-v", directory + ":/data", runtime,
                "dotnet", "/app/Writer.dll", "/data/" + database.name, variant],
                f"generate LiteDB {version} {variant} fixture", 300)
            archive = output / (name + ".zip")
            with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as compressed:
                compressed.write(database, database.name)
            manifest["fixtures"].append({"version": version, "variant": variant, "archive": archive.name,
                "sha256": hashlib.sha256(archive.read_bytes()).hexdigest(),
                "database_sha256": hashlib.sha256(database.read_bytes()).hexdigest(),
                "package_sha256": hashlib.sha256(package.read_bytes()).hexdigest(),
                "assembly_sha256": hashlib.sha256((binary / "LiteDB.dll").read_bytes()).hexdigest()})
    (output / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
