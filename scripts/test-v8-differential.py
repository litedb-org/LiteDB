#!/usr/bin/env python3
"""Fuzz ordinary v8 files across the current and released engines in separate processes."""
import argparse
import pathlib
import subprocess
import tempfile

root = pathlib.Path(__file__).resolve().parent.parent
projects = root / "tools" / "V8Differential"


def build(engine):
    subprocess.run([
        "dotnet", "build", str(projects / engine / (engine + ".csproj")),
        "--configuration", "Release", "-p:TestingEnabled=false",
    ], cwd=root, check=True)


def run(engine, *arguments):
    assembly = projects / engine / "bin" / "Release" / "net8.0" / (engine + ".dll")
    subprocess.run(["dotnet", str(assembly), *map(str, arguments)], cwd=root, check=True)


parser = argparse.ArgumentParser()
parser.add_argument("--seeds", type=int, default=3)
parser.add_argument("--operations", type=int, default=80)
args = parser.parse_args()
if args.seeds <= 0 or args.operations <= 0:
    parser.error("--seeds and --operations must be positive")

build("Current")
build("Legacy")
with tempfile.TemporaryDirectory(prefix="litedb-v8-differential-") as temporary:
    directory = pathlib.Path(temporary)
    for shard in range(args.seeds):
        seed = 2947 + shard * 1000003
        for creator, verifier in (("Legacy", "Current"), ("Current", "Legacy")):
            for encrypted in (False, True):
                password = "compatibility-secret" if encrypted else ""
                label = f"{creator.lower()}-{shard}-{'encrypted' if encrypted else 'plain'}"
                database = directory / (label + ".db")
                first = directory / (label + "-first.json")
                second = directory / (label + "-second.json")
                create_args = ["create", database, first, seed, 0]
                if password:
                    create_args.append(password)
                run(creator, *create_args)
                verify_args = ["verify", database, first, seed, 0]
                if password:
                    verify_args.append(password)
                run(verifier, *verify_args)
                mutate_args = ["mutate", database, second, seed ^ 0x51ED270B, args.operations]
                if password:
                    mutate_args.append(password)
                run(verifier, *mutate_args)
                verify_second = ["verify", database, second, seed, 0]
                if password:
                    verify_second.append(password)
                run(creator, *verify_second)
print(f"v8 differential: {args.seeds * 4} cross-engine cases passed")
