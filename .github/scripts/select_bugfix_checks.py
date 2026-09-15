"""Choose CI jobs by recomputing the trusted profile from immutable source."""

import json
import os
from pathlib import Path
import sys


CONTROL = Path(__file__).resolve().parents[1] / "bugfix"
sys.path.insert(0, str(CONTROL))
from patching import git
from profiles import build_profile
from state import require


def select_jobs(contract, profile, level, protocol, expected_digest):
    require(level in ("baseline", "focused", "broad", "acceptance"), "Unknown check level")
    require(protocol in ("compressed-v1", "legacy-six-lane-v1"), "Unknown check protocol")
    if level == "baseline":
        require(profile is None and not expected_digest, "Baseline cannot carry a candidate profile")
        approved = contract["environments"]
        for framework in ("net8.0", "net10.0"):
            for runner, prefixes in (("ubuntu-latest", ("linux-x64",)),
                                     ("windows-latest", ("windows-x64",)),
                                     ("macos-latest", ("macos-x64", "macos-arm64"))):
                if any(f"{prefix}-{framework}" in approved for prefix in prefixes):
                    return {"matrix": {"include": [{"os": runner, "framework": framework}]},
                            "production": False}
        raise ValueError("Baseline has no supported approved environment")
    require(isinstance(profile, dict) and profile.get("profile_sha256") == expected_digest
            and bool(expected_digest), "Candidate profile differs from the controller dispatch")
    complete = level == "acceptance" or (level == "broad" and protocol == "compressed-v1")
    matrix = profile["matrix"] if complete else profile["matrix"][:1]
    require(matrix, "Candidate profile selects no test jobs")
    return {"matrix": {"include": matrix}, "production": complete}


def main():
    root = Path.cwd()
    control = root / "control"
    require(git(control, "rev-parse", "HEAD") == os.environ["GITHUB_SHA"], "Trusted gate checkout changed")
    issue = int(os.environ["ISSUE"])
    contract = json.loads((control / "scripts/bugfix/issues.json").read_bytes())["issues"][str(issue)]
    candidate = os.environ.get("CANDIDATE_SHA", "")
    level = os.environ["LEVEL"]
    require(bool(candidate) == (level != "baseline"), "Candidate presence differs from check level")
    profile = None
    if candidate:
        require(git(root / "candidate", "rev-parse", "HEAD") == candidate, "Candidate checkout changed")
        profile = build_profile(control, root / "candidate",
                                {"issue": issue, "base_sha": os.environ["BASE_SHA"], "candidate_sha": candidate})
    result = select_jobs(contract, profile, level, os.environ["PROTOCOL"],
                         os.environ.get("ACCEPTANCE_PROFILE_SHA256", ""))
    with Path(os.environ["GITHUB_OUTPUT"]).open("a", encoding="utf-8") as output:
        for name, value in result.items():
            output.write(f"{name}={json.dumps(value, separators=(',', ':'))}\n")
    with Path(os.environ["GITHUB_STEP_SUMMARY"]).open("a", encoding="utf-8") as summary:
        summary.write("## Selected bugfix checks\n\n")
        summary.write(f"- Issue: {issue}\n- Test lanes: {len(result['matrix']['include'])}\n")
        summary.write(f"- Production build: {result['production']}\n")
        if profile:
            summary.write(f"- Compatibility: {profile['compatibility']}\n- Profile: `{profile['profile_sha256']}`\n")
    print(json.dumps(result))


if __name__ == "__main__":
    main()
