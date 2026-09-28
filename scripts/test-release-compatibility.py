#!/usr/bin/env python3
"""Verify pinned, real released databases. Missing or changed fixtures fail CI."""
import argparse
import hashlib
import io
import json
import pathlib
import subprocess
import tempfile
import urllib.request
import zipfile

root = pathlib.Path(__file__).resolve().parent.parent
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--artifacts", type=pathlib.Path, help="Local compatibility/released directory (otherwise use pinned GitHub files)")
parser.add_argument("--check-issue-attachment", action="store_true",
                    help="Also fetch and verify the original damaged deployment attachment (not required by CI)")
args = parser.parse_args()
revision = (root / "tools/ReleaseCompatibility/artifacts-revision.txt").read_text().strip()
base = "https://raw.githubusercontent.com/litedb-org/LiteDB-Artifacts/" + revision + "/compatibility/released/"


def read(name):
    if args.artifacts:
        return (args.artifacts / name).read_bytes()
    with urllib.request.urlopen(base + name, timeout=60) as response:
        return response.read()


manifest = json.loads(read("manifest.json"))
expected = {"4.0.0", *["4.1." + str(i) for i in range(5)], *["5.0." + str(i) for i in range(22)]}
actual = {(item["version"], item["variant"]) for item in manifest["fixtures"]}
if actual != {(v, variant) for v in expected for variant in ("plain", "encrypted")} or len(manifest["fixtures"]) != 56:
    raise RuntimeError("Incomplete/duplicate release inventory")
project = root / "tools/ReleaseCompatibility/Current/Current.csproj"
build_timeout = 300
try:
    subprocess.run(["dotnet", "build", str(project), "-c", "Release", "-p:TestingEnabled=true"],
                   check=True, timeout=build_timeout)
except subprocess.TimeoutExpired:
    raise SystemExit(f"Release compatibility build timed out after {build_timeout} seconds.") from None
runner = project.parent / "bin/Release/net8.0/Current.dll"
for fixture in manifest["fixtures"]:
    archive = read(fixture["archive"])
    if hashlib.sha256(archive).hexdigest() != fixture["sha256"]:
        raise RuntimeError("Archive hash mismatch: " + fixture["archive"])
    with tempfile.TemporaryDirectory(prefix="litedb-release-") as directory:
        with zipfile.ZipFile(io.BytesIO(archive)) as compressed:
            name = fixture["archive"].replace(".zip", ".db")
            if compressed.namelist() != [name]:
                raise RuntimeError("Unexpected archive members")
            data = compressed.read(name)
        if hashlib.sha256(data).hexdigest() != fixture["database_sha256"]:
            raise RuntimeError("Database hash mismatch: " + name)
        filename = pathlib.Path(directory) / name
        filename.write_bytes(data)
        subprocess.run(["dotnet", str(runner), str(filename), fixture["variant"], fixture["version"]], check=True, timeout=180)
print("PASS: all 56 released-writer databases")

# Required CI uses the immutable synthetic corpus and file-backed corruption
# regressions in LiteDB.Tests. An optional reproduction keeps the deployment
# attachment at its original URL without making its availability a CI dependency.
if not args.check_issue_attachment:
    raise SystemExit(0)

with urllib.request.urlopen("https://github.com/mbdavid/LiteDB/files/4405515/damaged.database.zip", timeout=60) as response:
    damaged = response.read()
if hashlib.sha256(damaged).hexdigest() != "a6e104bb90962d7d7ffb3eb049e7df455015a6cd59841a663b08d8ad5bf783d1":
    raise RuntimeError("Issue #1603/#3022 attachment hash mismatch")
with tempfile.TemporaryDirectory(prefix="litedb-3022-") as directory:
    filename = pathlib.Path(directory) / "VirtualFileSystem_litedb.db"
    with zipfile.ZipFile(io.BytesIO(damaged)) as compressed:
        filename.write_bytes(compressed.read(filename.name))
    subprocess.run(["dotnet", str(runner), "damaged", str(filename)], check=True, timeout=180)
