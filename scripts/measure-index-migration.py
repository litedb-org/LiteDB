#!/usr/bin/env python3
"""Measure actual 5.0.21 -> current migration; synthetic data is not a production SLO."""
import argparse
import os
import pathlib
import subprocess
import tempfile

parser = argparse.ArgumentParser()
parser.add_argument("--documents", type=int, default=100000)
args = parser.parse_args()
if args.documents <= 0:
    parser.error("--documents must be positive")
root = pathlib.Path(__file__).resolve().parent.parent
projects = root / "tools/IndexCompatibility"


def run(engine, *arguments):
    runner = projects / engine / f"bin/Release/net8.0/{engine}.dll"
    subprocess.run(["dotnet", str(runner), *map(str, arguments)], check=True,
                   env={**os.environ, "DOTNET_TieredCompilation": "0"})


for engine in ("Legacy", "Current"):
    subprocess.run(["dotnet", "build", str(projects / engine / f"{engine}.csproj"),
                    "-c", "Release", "-p:TestingEnabled=false"], check=True)
    print(engine + " numeric operations", flush=True)
    run(engine, "measure-numbers")

with tempfile.TemporaryDirectory(prefix="litedb-migration-measurement-") as directory:
    for encryption in ("plain", "encrypted"):
        filename = pathlib.Path(directory) / (encryption + ".db")
        run("Legacy", "measure-create", filename, args.documents, encryption)
        run("Current", "measure-open", filename, args.documents, encryption)
