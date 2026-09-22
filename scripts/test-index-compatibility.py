#!/usr/bin/env python3
"""Produce real 5.0.21 indexes, migrate them, and verify the downgrade barrier."""
import pathlib
import subprocess
import tempfile

root = pathlib.Path(__file__).resolve().parent.parent
projects = root / "tools" / "IndexCompatibility"


def run(engine, mode, directory):
    subprocess.run([
        "dotnet", "run", "--project", str(projects / engine / (engine + ".csproj")),
        "--configuration", "Release", "-p:TestingEnabled=true", "--", mode, directory,
    ], cwd=root, check=True)


with tempfile.TemporaryDirectory(prefix="litedb-index-compatibility-") as directory:
    run("Legacy", "create", directory)
    run("Current", "migrate", directory)
    run("Legacy", "refuse", directory)
    run("Current", "verify", directory)
