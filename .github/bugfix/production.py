"""Build the exact candidate and run compatibility only when its trusted profile requires it."""

import argparse
import json
import os
from pathlib import Path
import subprocess
import sys

from patching import git
from profiles import build_profile
from state import require
from compiler_feedback import write_build_report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("repository", "control", "output"):
        parser.add_argument("--" + name, type=Path, required=True)
    for name in ("base-sha", "candidate-sha", "profile-sha256"):
        parser.add_argument("--" + name, required=True)
    parser.add_argument("--issue", type=int, required=True)
    args = parser.parse_args()
    state = {"issue": args.issue, "base_sha": args.base_sha, "candidate_sha": args.candidate_sha}
    require(git(args.control, "rev-parse", "HEAD") == os.environ["GITHUB_SHA"], "Production verifier checkout changed")
    require(git(args.repository, "rev-parse", "HEAD") == args.candidate_sha, "Production checkout differs from tested candidate")
    profile = build_profile(args.control, args.repository, state)
    require(profile["profile_sha256"] == args.profile_sha256, "Production profile differs from dispatch")
    args.output.parent.mkdir(parents=True, exist_ok=True)
    commands = [
        ("size", [sys.executable, str(args.control / "scripts/check-csharp-size.py"), "--base", args.base_sha]),
        ("build", ["dotnet", "build", "LiteDB/LiteDB.csproj", "-c", "Release", "-p:TestingEnabled=false", "-p:GitVersionEnabled=false"]),
    ]
    if profile["compatibility"]:
        commands.append(("compatibility", [sys.executable, str(args.repository / "scripts/test-vector-compatibility.py")]))
    for name, command in commands:
        log_path = args.output.parent / f"production-{name}.log"
        try:
            with log_path.open("w", encoding="utf-8") as log:
                subprocess.run(command, cwd=args.repository, stdout=log, stderr=subprocess.STDOUT, check=True, timeout=600,
                               env={**os.environ, "DOTNET_CLI_UI_LANGUAGE": "en-US", "VSLANG": "1033"})
        except (subprocess.CalledProcessError, subprocess.TimeoutExpired) as error:
            if name == "build":
                contract = json.loads((args.control / "scripts/bugfix/issues.json").read_bytes())["issues"][str(args.issue)]
                identity = {"issue": args.issue, "source_sha": args.candidate_sha,
                            "test_source_sha": contract["frozen_test_revision"], "workflow_sha": os.environ["GITHUB_SHA"],
                            "build_kind": "production", "framework": "all-production-targets"}
                write_build_report(args.output.parent / "production-build-report.json", log_path, args.repository,
                                   identity, getattr(error, "returncode", None), isinstance(error, subprocess.TimeoutExpired))
            raise
    report = {"schema_version": 1, **state, "workflow_sha": os.environ["GITHUB_SHA"],
              "acceptance_profile": profile, "accepted": True, "production_build": True,
              "compatibility": profile["compatibility"]}
    args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
