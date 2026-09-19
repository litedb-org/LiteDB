#!/usr/bin/env python3
"""Check the file-format boundary with separate current and released-engine processes."""
import pathlib
import subprocess
import tempfile

root = pathlib.Path(__file__).resolve().parent.parent
projects = root / "tools" / "VectorCompatibility"


def run(engine, *arguments):
    subprocess.run([
        "dotnet", "run", "--project", str(projects / engine / (engine + ".csproj")),
        "--configuration", "Release", "-p:TestingEnabled=true", "--", *arguments,
    ], cwd=root, check=True)


with tempfile.TemporaryDirectory(prefix="litedb-vector-compatibility-") as directory:
    run("Current", "create", directory)
    run("Legacy", "create", directory)
    run("Current", "ordinary", directory)
    run("Legacy", "ordinary", directory)
    run("Current", "promote", directory)
    run("Legacy", "promoted", directory)
    run("Current", "verify", directory)
