"""Run the original date fixture and independent oracle in separate timezone processes."""
import os
import pathlib
import subprocess

root = pathlib.Path(__file__).resolve().parents[2]
for zone in ("Etc/UTC", "Asia/Tokyo", "America/New_York"):
    environment = dict(os.environ, TZ=zone)
    print("Testing timezone:", zone, flush=True)
    subprocess.run([
        "dotnet", "test", "LiteDB.Tests", "-c", "Release", "-f", "net8.0", "--no-build",
        "--settings", "tests.runsettings", "--filter",
        "FullyQualifiedName~Expressions_Scalar_Methods|FullyQualifiedName~Issue2414_",
    ], cwd=root, env=environment, check=True, timeout=60)
