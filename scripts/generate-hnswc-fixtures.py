#!/usr/bin/env python3
"""Produce PR #3142 fixtures with the real pre-fix writer in an isolated worktree."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import zipfile

WRITER = "6b2ecd3af26829c52d964da9dc0c33d67c7753a4"
ROOT = Path(__file__).resolve().parents[1]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True, help="Fresh output directory (DBs, ZIPs, producer TRX and hashes).")
    args = parser.parse_args()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    if any(output.iterdir()):
        parser.error("--output must be empty; existing evidence is never overwritten")
    temporary = Path(tempfile.mkdtemp(prefix="hnswc-producer-"))
    checkout = temporary / "writer"
    subprocess.run(["git", "worktree", "add", "--detach", str(checkout), WRITER], cwd=ROOT, check=True)
    shutil.copy2(ROOT / "tools/HnswFixtures/FixtureGenerator.cs", checkout / "LiteDB.Tests/Query/HnswcFixtureGenerator_Tests.cs")
    environment = dict(os.environ, LITEDB_HNSW_FIXTURE_OUTPUT=str(output))
    # Focused producer only, never the whole suite or a benchmark. Keep the
    # repository's timeout and hook configuration; preserve worktree on failure.
    subprocess.run([
        "dotnet", "test", "LiteDB.Tests", "-c", "Release", "-f", "net8.0",
        "-p:TestingEnabled=true", "--settings", "tests.runsettings",
        "--filter", "FullyQualifiedName~Generate_Hnsw_", "--logger", "trx;LogFileName=producer.trx",
        "--results-directory", str(output),
    ], cwd=checkout, env=environment, check=True)
    produced = {}
    for database in sorted(output.glob("*.db")):
        archive = database.with_suffix(".zip")
        with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as target:
            target.write(database, "vectors.db")
        produced[archive.name] = {"sha256": hashlib.sha256(archive.read_bytes()).hexdigest(),
                                  "databaseSha256": hashlib.sha256(database.read_bytes()).hexdigest()}
    (output / "producer.json").write_text(json.dumps({"writer": WRITER, "files": produced}, indent=2) + "\n")
    print(f"Fixtures and producer evidence: {output}")
    print(f"Producer checkout retained for audit: {checkout}")
    print("Publish to LiteDB-Artifacts; pin its commit and archive hashes in Resources/artifacts.json.")


if __name__ == "__main__":
    main()
