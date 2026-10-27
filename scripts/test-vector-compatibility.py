#!/usr/bin/env python3
"""Check the file-format boundary with separate current and released-engine processes."""
import pathlib
import subprocess
import tempfile
import shutil

root = pathlib.Path(__file__).resolve().parent.parent
projects = root / "tools" / "VectorCompatibility"


def run(engine, *arguments):
    subprocess.run([
        "dotnet", "run", "--project", str(projects / engine / (engine + ".csproj")),
        "--configuration", "Release", "-p:TestingEnabled=true", "--", *arguments,
    ], cwd=root, check=True)


with tempfile.TemporaryDirectory(prefix="litedb-vector-compatibility-") as directory:
    run("Current", "background-create", directory)
    run("Legacy", "background", directory)
    run("Current", "background-verify", directory)
    # Genuine pre-worker production writer, pinned before the feature existed.
    revision = "197b6b7f832488cfd812d374398fb7f85095c06d"
    older = pathlib.Path(directory) / "pre-worker"
    subprocess.run(["git", "fetch", "--no-tags", "origin", revision], cwd=root, check=True, timeout=120)
    subprocess.run(["git", "worktree", "add", "--detach", str(older), revision], cwd=root, check=True, timeout=120)
    try:
        probe = pathlib.Path(directory) / "input-probe"
        shutil.copytree(root / "tools/BackgroundCheckpointInput", probe, ignore=shutil.ignore_patterns("bin", "obj"))
        subprocess.run([
            "dotnet", "run", "--project", str(probe / "Input.csproj"), "-c", "Release",
            "-p:TestingEnabled=false", "-p:GitVersionEnabled=false", "-p:TargetFrameworks=net8.0",
            "-p:CandidateProject=" + str(older / "LiteDB/LiteDB.csproj"), "--", directory,
        ], cwd=root, check=True, timeout=300)
        run("Current", "background-before-verify", directory)
    finally:
        subprocess.run(["git", "worktree", "remove", "--force", str(older)], cwd=root, check=True, timeout=120)
    run("Current", "padding-create", directory)
    run("Legacy", "padding", directory)
    run("Current", "padding-verify", directory)
    run("Current", "reclaim-create", directory)
    run("Legacy", "reclaim", directory)
    run("Current", "reclaim-verify", directory)
    run("Current", "create", directory)
    run("Legacy", "create", directory)
    run("Current", "readonly", directory)
    run("Current", "interrupt", directory)
    run("Legacy", "resume", directory)
    run("Current", "resumed", directory)
    run("Current", "resumed-wal", directory)
    run("Current", "interrupt-unsealed", directory)
    run("Legacy", "resume-unsealed", directory)
    run("Current", "resumed-unsealed", directory)
    run("Current", "convert", directory)
    run("Legacy", "converted", directory)
    run("Current", "verify", directory)
