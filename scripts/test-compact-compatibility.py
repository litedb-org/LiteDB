#!/usr/bin/env python3
"""Generate v8/mixed-v10/array-only-v10 fixtures, verify old-engine rejection and downgrade."""
import pathlib
import subprocess
import tempfile

root = pathlib.Path(__file__).resolve().parent.parent
projects = root / "tools" / "VectorCompatibility"


def run(engine, mode, directory):
    subprocess.run([
        "dotnet", "run", "--project", str(projects / engine / (engine + ".csproj")),
        "--configuration", "Release", "-p:TestingEnabled=true", "--", mode, directory,
    ], cwd=root, check=True)


with tempfile.TemporaryDirectory(prefix="litedb-compact-compatibility-") as directory:
    run("Legacy", "compact-create", directory)
    run("Current", "compact-promote", directory)
    run("Legacy", "compact-reject", directory)
    run("Current", "compact-downgrade", directory)
    run("Legacy", "compact-verify", directory)
