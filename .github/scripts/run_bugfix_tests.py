"""Build a pinned source checkout and retain complete test evidence for a gate."""

import argparse
import json
import os
from pathlib import Path
import platform
import subprocess
import sys
import time


def execute(command, repository, log, timeout):
    started = time.monotonic()
    environment = {**os.environ, "DOTNET_CLI_UI_LANGUAGE": "en-US", "VSLANG": "1033"}
    with log.open("w", encoding="utf-8") as output:
        result = subprocess.run(command, cwd=repository, stdout=output,
                                stderr=subprocess.STDOUT, timeout=timeout, env=environment)
    print(f"{log.name}: exit={result.returncode}, seconds={time.monotonic() - started:.1f}",
          flush=True)
    if result.returncode not in (0, 1):
        raise RuntimeError(f"Unexpected process status; inspect {log.name}")
    return result.returncode


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repository", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--manifest", required=True)
    parser.add_argument("--issue", required=True)
    parser.add_argument("--framework", default="net8.0")
    parser.add_argument("--level", choices=("focused", "broad"), default="focused")
    args = parser.parse_args()
    repository = Path(args.repository).resolve()
    output = Path(args.output).resolve()
    output.mkdir(parents=True, exist_ok=False)
    contract = json.loads(Path(args.manifest).read_text(encoding="utf-8"))["issues"][args.issue]
    revision = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=repository,
                                       text=True).strip()
    frozen = contract["frozen_test_revision"]
    subprocess.run(["git", "diff", "--exit-code", frozen, revision, "--", "LiteDB.Tests"],
                   cwd=repository, check=True, stdout=subprocess.DEVNULL)
    project = "LiteDB.Tests/LiteDB.Tests.csproj"
    properties = ["-p:TestingEnabled=true", "-p:GitVersionEnabled=false",
                  "-p:TargetFrameworks=" + args.framework]
    build = ["dotnet", "build", project, "-c", "Release", "-f", args.framework,
             "--nologo", *properties]
    if execute(build, repository, output / "build.log", 600) != 0:
        raise RuntimeError("Build failed; this is not a bug reproduction")
    common = ["dotnet", "test", project, "-c", "Release", "-f", args.framework,
              "--no-build", "--no-restore", "--settings", "tests.runsettings",
              "--results-directory", str(output), "--nologo", *properties]
    if args.level == "broad":
        if execute(common + ["--list-tests"], repository, output / "discovery.log", 120) != 0:
            raise RuntimeError("Independent full test discovery failed")
        discovery = (output / "discovery.log").read_text(encoding="utf-8-sig")
        marker = "The following Tests are available:"
        if discovery.count(marker) != 1:
            raise RuntimeError("Expected exactly one complete test discovery section")
        names = [line.strip() for line in discovery.split(marker, 1)[1].splitlines() if line.strip()]
        if not names or any(not name.startswith("LiteDB.") for name in names):
            raise RuntimeError("Unexpected full test discovery output")
        (output / "test-inventory.json").write_text(
            json.dumps({"schema_version": 1, "tests": names}, indent=2, ensure_ascii=False) + "\n",
            encoding="utf-8")
    result = {"schema_version": 1, "source_sha": revision,
              "test_source_sha": frozen, "framework": args.framework,
              "runner_os": platform.system(), "runner_arch": platform.machine(),
              "workflow_sha": os.environ.get("GITHUB_SHA"), "runs": {}}
    for name in (["focused", "broad"] if args.level == "broad" else ["focused"]):
        command = common + ["--logger", f"trx;LogFileName={name}.trx"]
        if name == "focused":
            command += ["--filter", contract["filter"]]
        result["runs"][name] = execute(command, repository, output / f"{name}.log", 360)
        if not (output / f"{name}.trx").is_file():
            raise RuntimeError(f"Missing {name} TRX report")
    (output / "execution.json").write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(result), flush=True)


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, KeyError, subprocess.SubprocessError, RuntimeError) as error:
        print(f"Harness failure: {error}", file=sys.stderr)
        sys.exit(1)
