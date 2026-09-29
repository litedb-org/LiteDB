#!/usr/bin/env python3
"""Generate immutable, real released-writer files; run only when updating the corpus."""
import hashlib
import json
import pathlib
import subprocess
import sys
import tempfile
import urllib.request
import zipfile

root = pathlib.Path(__file__).resolve().parent.parent
output = pathlib.Path(sys.argv[1]).resolve()
output.mkdir(parents=True, exist_ok=True)
with urllib.request.urlopen("https://api.nuget.org/v3-flatcontainer/litedb/index.json") as response:
    versions = [v for v in json.load(response)["versions"] if v.startswith(("4.", "5.")) and "-" not in v]
runtime = "mcr.microsoft.com/dotnet/runtime@sha256:96d655c489cdf341f7679510efe51a5ba4e174d74aaaf613f7578f2e25c3717d"
subprocess.run(["docker", "pull", runtime], check=True)
manifest = {"schema": 1, "source": "https://api.nuget.org/v3-flatcontainer/litedb/index.json",
    "writer_runtime": runtime, "generator_sha256": hashlib.sha256(pathlib.Path(__file__).read_bytes()).hexdigest(),
    "corpus_sha256": hashlib.sha256((root / "tools/ReleaseCompatibility/Corpus.cs").read_bytes()).hexdigest(), "fixtures": []}
for version in versions:
    project = root / "tools/ReleaseCompatibility/Writer/Writer.csproj"
    subprocess.run(["dotnet", "build", str(project), "-c", "Release", "-p:ReleaseVersion=" + version], check=True)
    package = pathlib.Path.home() / ".nuget/packages/litedb" / version / ("litedb." + version + ".nupkg")
    for variant in ("plain", "encrypted"):
        name = "litedb-" + version + "-" + variant
        with tempfile.TemporaryDirectory() as directory:
            database = pathlib.Path(directory) / (name + ".db")
            binary = project.parent / "bin/Release/netcoreapp3.1"
            subprocess.run(["docker", "run", "--rm", "--network", "none",
                "-v", str(binary) + ":/app:ro", "-v", directory + ":/data", runtime,
                "dotnet", "/app/Writer.dll", "/data/" + database.name, variant], check=True)
            archive = output / (name + ".zip")
            with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as compressed:
                compressed.write(database, database.name)
            manifest["fixtures"].append({"version": version, "variant": variant, "archive": archive.name,
                "sha256": hashlib.sha256(archive.read_bytes()).hexdigest(),
                "database_sha256": hashlib.sha256(database.read_bytes()).hexdigest(),
                "package_sha256": hashlib.sha256(package.read_bytes()).hexdigest(),
                "assembly_sha256": hashlib.sha256((binary / "LiteDB.dll").read_bytes()).hexdigest()})
    (output / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
