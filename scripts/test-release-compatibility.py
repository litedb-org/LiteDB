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
args = parser.parse_args()
tools = root / "tools/ReleaseCompatibility"
revision = (tools / "artifacts-revision.txt").read_text().strip()
base = "https://raw.githubusercontent.com/litedb-org/LiteDB-Artifacts/" + revision + "/compatibility/released/"


def read(name):
    if args.artifacts:
        return (args.artifacts / name).read_bytes()
    with urllib.request.urlopen(base + name, timeout=60) as response:
        return response.read()


def source_sha256(path):
    # The generator hashed LF checkouts; Windows runners may check out CRLF.
    return hashlib.sha256(path.read_bytes().replace(b"\r\n", b"\n")).hexdigest()


manifest = json.loads(read("manifest.json"))
expected = {"4.0.0", *["4.1." + str(i) for i in range(5)], *["5.0." + str(i) for i in range(22)]}
actual = {(item["version"], item["variant"]) for item in manifest["fixtures"]}
if actual != {(v, variant) for v in expected for variant in ("plain", "encrypted")} or len(manifest["fixtures"]) != 56:
    raise RuntimeError("Incomplete/duplicate release inventory")
# Expected documents come from Corpus.cs; it must be the source the writers used.
if source_sha256(tools / "Corpus.cs") != manifest["corpus_sha256"]:
    raise RuntimeError("Corpus.cs differs from the source that generated revision " + revision)
if source_sha256(root / "scripts/generate-release-corpus.py") != manifest["generator_sha256"]:
    raise RuntimeError("generate-release-corpus.py differs from the generator of revision " + revision)

# Fixtures whose probe fails until a later engine fix. Each is still run and must
# fail with its recorded diagnostic, so a fix forces activation.
pending = {(p["version"], p["variant"]): p for p in json.loads((tools / "pending-activation.json").read_text())["pending"]}
if not set(pending) <= actual:
    raise RuntimeError("pending-activation.json names fixtures outside the inventory")
project = tools / "Current/Current.csproj"
build_timeout = 300
try:
    subprocess.run(["dotnet", "build", str(project), "-c", "Release", "-p:TestingEnabled=false"],
                   check=True, timeout=build_timeout)
except subprocess.TimeoutExpired:
    raise SystemExit(f"Release compatibility build timed out after {build_timeout} seconds.") from None
runner = project.parent / "bin/Release/net8.0/Current.dll"
passed = []
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
        command = ["dotnet", str(runner), str(filename), fixture["variant"], fixture["version"]]
        key = (fixture["version"], fixture["variant"])
        if key not in pending:
            subprocess.run(command, check=True, timeout=180)
            passed.append(key)
            continue
        result = subprocess.run(command, capture_output=True, text=True, timeout=180)
        output = result.stdout + result.stderr
        if result.returncode == 0:
            raise RuntimeError(f"{name} now passes; remove it from pending-activation.json ({pending[key]['owner']})")
        if pending[key]["expected"] not in output:
            print(output[-4000:])
            raise RuntimeError(f"{name} failed without its recorded diagnostic '{pending[key]['expected']}'")
        if hashlib.sha256(filename.read_bytes()).hexdigest() != fixture["database_sha256"]:
            raise RuntimeError("Rejected open mutated original: " + name)
        print(f"PENDING {key[0]} {key[1]} ({pending[key]['owner']}): still fails with the recorded diagnostic", flush=True)
print(f"PASS: {len(passed)} released-writer databases verified; {len(pending)} pending activation; 56 hashes verified")
