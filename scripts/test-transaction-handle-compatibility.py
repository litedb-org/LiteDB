#!/usr/bin/env python3
"""Check that #3064's transaction handles keep existing consumers source- and binary-compatible.

The fixture in tools/TransactionHandleValidation is compiled once against the parent
LiteDB.dll and then run, unchanged, against the candidate DLL:

- binary: implementations of the pre-handle ILiteDatabase and ILiteEngine interfaces load
  and work, legacy transactions work through LiteDatabase, a decorator and a caller-owned
  engine (disposeOnClose: false), and the candidate refuses handles for providers that do
  not support them before any side effect;
- parity: a transcript of legacy-API outcomes (nesting, rollback, statement errors,
  foreign-thread completion, read-only, Direct and Shared files) is identical on both.

Then the same source is rebuilt against the candidate: with TreatWarningsAsErrors it must
fail on CS0618 (against a pre-handle parent it must build), the documented WarningsNotAsErrors=CS0618
exception must build, and a consumer of the new API must build warning-free and run.

Use production DLLs (TestingEnabled=false) of the same target framework as --framework.
"""
import argparse
import hashlib
import json
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import time

SOURCE = Path(__file__).resolve().parent.parent / "tools" / "TransactionHandleValidation"
BINARY_PASS = "PASS precompiled old ILiteDatabase implementation and legacy consumer"
NEW_API_PASS = "PASS new transaction API consumer"


class Runner:
    def __init__(self, output):
        self.output = output
        self.results = []

    def run(self, name, command, expected=0, contains=(), absent=(), timeout=600):
        started = time.monotonic()
        try:
            result = subprocess.run(command, capture_output=True, text=True, errors="replace", timeout=timeout)
            code, text = result.returncode, result.stdout + result.stderr
        except subprocess.TimeoutExpired as error:
            code, text = "timeout", f"{error.stdout or ''}{error.stderr or ''}\ntimed out after {timeout}s"
        log = self.output / (name + ".log")
        log.write_text(text, encoding="utf-8")
        problems = [] if code == expected else [f"exit {code}, expected {expected}"]
        problems += [f"missing {item!r}" for item in contains if item not in text]
        problems += [f"unexpected {item!r}" for item in absent if re.search(item, text)]
        self.results.append({"name": name, "command": [str(part) for part in command], "expected": expected,
                             "exit": code, "seconds": round(time.monotonic() - started, 1), "log": log.name,
                             "problems": problems})
        if problems:
            raise RuntimeError(f"{name}: {'; '.join(problems)}; see {log}")
        print(f"ok   {name} (exit {code})", flush=True)
        return text


def sha256(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def loaded_library(text):
    match = re.search(r"^loaded=(.+?)\s*$", text, re.MULTILINE)
    return Path(match.group(1)) if match else None


def build_command(project, library, framework, output, *extra):
    # Built from a copy outside the repository; never import repository build props.
    return ["dotnet", "build", str(project), "-c", "Release", "-nologo", "-tl:off", f"-p:FixtureFramework={framework}",
            f"-p:LibraryPath={library}", "-p:ImportDirectoryBuildProps=false",
            "-p:ImportDirectoryBuildTargets=false", "-o", str(output), *extra]


def copy_source(root, name):
    target = root / name
    shutil.copytree(SOURCE, target, ignore=shutil.ignore_patterns("bin", "obj"))
    return target


def check(runner, args, root):
    parent_dll, candidate_dll, framework = args.parent_dll, args.candidate_dll, args.framework
    hashes = {"parent": sha256(parent_dll), "candidate": sha256(candidate_dll)}
    if hashes["parent"] == hashes["candidate"]:
        raise RuntimeError("the parent and candidate DLLs are identical")

    consumer = copy_source(root, "consumer") / "TransactionHandleValidation.csproj"
    binaries = {"parent": root / "parent", "candidate": root / "candidate"}
    runner.run("build-parent-consumer", build_command(consumer, parent_dll, framework, binaries["parent"]),
               contains=("Build succeeded",))
    shutil.copytree(binaries["parent"], binaries["candidate"])
    for stale in ("LiteDB.pdb", "LiteDB.xml"):
        (binaries["candidate"] / stale).unlink(missing_ok=True)
    shutil.copy2(candidate_dll, binaries["candidate"] / "LiteDB.dll")

    transcripts, parent_api = {}, None
    for version, directory in binaries.items():
        fixture = str(directory / "TransactionHandleValidation.dll")
        # The candidate must have the handle API; a parent that already has it (after #3064
        # merged) is still checked for binary compatibility and parity.
        api = () if version == "parent" else ("handle-api=present",)
        text = runner.run(f"{version}-binary", ["dotnet", fixture, "binary"], contains=(BINARY_PASS, *api), timeout=300)
        if version == "parent":
            parent_api = "handle-api=present" in text
        library = loaded_library(text)
        if library is None or sha256(library) != hashes[version]:
            raise RuntimeError(f"{version}-binary loaded {library}, not the {version} DLL")
        text = runner.run(f"{version}-parity", ["dotnet", fixture, "parity"], timeout=300)
        transcripts[version] = [line for line in text.splitlines() if line.startswith("parity:")]

    (runner.output / "parity-parent.txt").write_text("\n".join(transcripts["parent"]) + "\n", encoding="utf-8")
    (runner.output / "parity-candidate.txt").write_text("\n".join(transcripts["candidate"]) + "\n", encoding="utf-8")
    if not transcripts["parent"] or transcripts["parent"] != transcripts["candidate"]:
        differences = [f"parent:    {old}\ncandidate: {new}"
                       for old, new in zip(transcripts["parent"], transcripts["candidate"]) if old != new]
        raise RuntimeError("legacy outcomes differ:\n" + "\n".join(differences or ["(transcript length differs)"]))
    print(f"ok   legacy-parity ({len(transcripts['parent'])} identical outcome lines)", flush=True)

    strict = ["-p:TreatWarningsAsErrors=true"]
    parent_strict = build_command(copy_source(root, "strict-parent") / consumer.name, parent_dll, framework,
                                  root / "strict-parent-out", *strict)
    if parent_api:
        # The parent already deprecates the legacy API; the strict consumer fails there too.
        runner.run("parent-warnings-as-errors", parent_strict, expected=1, contains=("error CS0618",))
    else:
        runner.run("parent-warnings-as-errors", parent_strict, contains=("Build succeeded",), absent=(r"CS0618",))
    strict_candidate = copy_source(root, "strict-candidate") / consumer.name
    runner.run("warnings-as-errors",
               build_command(strict_candidate, candidate_dll, framework, root / "strict-candidate-out", *strict),
               expected=1, contains=("error CS0618",))
    runner.run("targeted-warning-exception",
               build_command(copy_source(root, "targeted") / consumer.name, candidate_dll, framework,
                             root / "targeted-out", *strict, "-p:WarningsNotAsErrors=CS0618"),
               contains=("Build succeeded", "warning CS0618"), absent=(r"error CS",))

    new_api = copy_source(root, "new-api") / "NewApi" / "NewApiConsumer.csproj"
    runner.run("new-api-warnings-as-errors",
               build_command(new_api, candidate_dll, framework, root / "new-api-out", *strict),
               contains=("Build succeeded",), absent=(r"warning CS\d+", r"error CS\d+"))
    text = runner.run("new-api-run", ["dotnet", str(root / "new-api-out" / "NewApiConsumer.dll")],
                      contains=(NEW_API_PASS,), timeout=300)
    library = loaded_library(text)
    if library is None or sha256(library) != hashes["candidate"]:
        raise RuntimeError(f"new-api-run loaded {library}, not the candidate DLL")
    return hashes, parent_api


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--parent-dll", type=Path, required=True, help="Production LiteDB.dll of the PR base")
    parser.add_argument("--candidate-dll", type=Path, required=True, help="Production LiteDB.dll of the PR head")
    parser.add_argument("--framework", default="net8.0", help="Target framework of both DLLs and the fixture")
    parser.add_argument("--output", type=Path, required=True, help="Directory for logs and manifest.json")
    args = parser.parse_args()
    args.parent_dll, args.candidate_dll = args.parent_dll.resolve(), args.candidate_dll.resolve()
    args.output.mkdir(parents=True, exist_ok=True)
    runner = Runner(args.output)
    manifest = {"framework": args.framework, "parent_dll": str(args.parent_dll),
                "candidate_dll": str(args.candidate_dll), "status": "fail"}
    try:
        manifest["sdk"] = subprocess.run(["dotnet", "--version"], capture_output=True, text=True).stdout.strip()
        with tempfile.TemporaryDirectory(prefix="litedb-handle-api-") as temporary:
            hashes, parent_api = check(runner, args, Path(temporary))
        manifest.update(parent_sha256=hashes["parent"], candidate_sha256=hashes["candidate"],
                        parent_has_handle_api=parent_api, status="pass")
    except RuntimeError as error:
        manifest["error"] = str(error)
        print(f"FAIL {error}", file=sys.stderr)
    finally:
        manifest["results"] = runner.results
        (args.output / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    if manifest["status"] != "pass":
        return 1
    print(f"{BINARY_PASS} (parent and candidate, {args.framework})")
    print("PASS legacy outcomes identical; CS0618 fails the warnings-as-errors consumer; "
          "WarningsNotAsErrors=CS0618 builds; new API builds warning-free and runs")
    return 0


if __name__ == "__main__":
    sys.exit(main())
