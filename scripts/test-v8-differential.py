#!/usr/bin/env python3
"""Compare generated workloads across released v8 and current checksum files."""
import argparse
import pathlib
import subprocess
import shutil
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
        for encrypted in (False, True):
            password = "compatibility-secret" if encrypted else ""
            label = f"{shard}-{'encrypted' if encrypted else 'plain'}"
            legacy = directory / (label + "-legacy.db")
            converted = directory / (label + "-converted.db")
            fresh = directory / (label + "-fresh.db")
            baseline = directory / (label + "-baseline.json")
            expected = directory / (label + "-expected.json")
            actual = directory / (label + "-actual.json")

            def invoke(engine, action, file, snapshot, operation_seed=seed, operations=0):
                arguments = [action, file, snapshot, operation_seed, operations]
                if password:
                    arguments.append(password)
                run(engine, *arguments)

            invoke("Legacy", "create", legacy, baseline)
            shutil.copyfile(legacy, converted)
            before = converted.read_bytes()
            invoke("Current", "needs-migration", converted, baseline)
            if converted.read_bytes() != before:
                raise AssertionError("Read-only legacy verification changed its file")
            mutation_seed = seed ^ 0x51ED270B
            invoke("Legacy", "mutate", legacy, expected, mutation_seed, args.operations)
            invoke("Current", "mutate", converted, actual, mutation_seed, args.operations)
            if expected.read_bytes() != actual.read_bytes():
                raise AssertionError("Converted workload differs from the released engine")
            invoke("Current", "verify", converted, expected)
            invoke("Legacy", "reject", converted, expected)
            before = legacy.read_bytes()
            invoke("Current", "needs-migration", legacy, expected)
            if legacy.read_bytes() != before:
                raise AssertionError("Read-only legacy verification changed its file")
            invoke("Current", "create", fresh, actual)
            if baseline.read_bytes() != actual.read_bytes():
                raise AssertionError("New checksum file differs from the released engine")
            invoke("Legacy", "reject", fresh, baseline)
            invoke("Current", "mutate", fresh, actual, mutation_seed, args.operations)
            if expected.read_bytes() != actual.read_bytes():
                raise AssertionError("New checksum workload differs from the released engine")
            invoke("Current", "verify", fresh, expected)
print(f"v8/checksum differential: {args.seeds * 2} plain/encrypted cases passed (converted and new files)")
