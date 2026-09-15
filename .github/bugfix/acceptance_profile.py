"""Select compressed per-fix acceptance from trusted contracts and immutable diffs."""

import argparse
import fnmatch
import hashlib
import json
from pathlib import Path
import re
import subprocess
import sys


POLICY = {
    "version": "compressed-acceptance-v3",
    "ordinary_pairs": {
        "2874": ["LiteDB/Document/ObjectId.cs"],
        "1506": ["LiteDB/Client/Database/Collections/Find.cs"],
        "2839": ["LiteDB/Client/Database/Collections/Aggregate.cs"],
        "2869": ["LiteDB/Document/BsonValue.cs"],
        "2770": ["LiteDB/Client/Mapper/Linq/LinqExpressionVisitor.cs"],
        "2779": ["LiteDB/Client/Mapper/Linq/LinqExpressionVisitor.cs"],
        "2847": ["LiteDB/Client/Mapper/Linq/TypeResolver/StringResolver.cs"],
        "2205": ["LiteDB/Document/Expression/Parser/BsonExpressionParser.cs"],
        "2871": ["LiteDB/Client/Mapper/Reflection/Reflection.cs"],
    },
    "compatibility_pairs": {
        "2867": {"paths": ["LiteDB/Client/Mapper/BsonMapper.GetEntityMapper.cs",
                           "LiteDB/Client/Mapper/Linq/LinqExpressionVisitor.cs"],
                 "tests": ["LiteDB.Tests.Mapper.MapperInheritance_Tests",
                           "LiteDB.Tests.Database.AutoId_Tests"]},
        "1002": {"paths": ["LiteDB/Client/Database/Collections/Insert.cs"],
                 "tests": ["LiteDB.Tests.Database.AutoId_Tests"]},
        "2802": {"paths": ["LiteDB/Client/Database/LiteQueryable.cs"],
                 "tests": ["LiteDB.Tests.Mapper.Mapper_Tests",
                           "LiteDB.Tests.Database.FindAll_Tests"]},
    },
    "rules": [
        {"id": "storage", "paths": ["LiteDB/Engine/Disk/*", "LiteDB/Engine/Pages/*",
         "LiteDB/Engine/Services/*", "LiteDB/Engine/Structures/*", "LiteDB/Engine/Engine/*",
         "LiteDB/Engine/FileReader/*", "LiteDB/Client/Storage/*"],
         "tests": ["LiteDB.Tests.Engine.Transactions_Tests", "LiteDB.Internals.WalFailureCleanup_Tests"]},
        {"id": "serialization", "paths": ["LiteDB/Document/Bson/*", "LiteDB/Document/Json/*",
         "LiteDB/Engine/Disk/Serializer/*"], "tests": ["LiteDB.Tests.Document.Bson_Tests"]},
        {"id": "upgrade", "paths": ["LiteDB/Engine/FileReader/*", "LiteDB/*Upgrade*"],
         "tests": ["LiteDB.Tests.Database.Upgrade_Tests"], "windows": True},
        {"id": "encryption-streams", "paths": ["LiteDB/*Aes*", "LiteDB/*Stream*",
         "LiteDB/Utils/FileHelper.cs"], "tests": ["LiteDB.Internals.Aes_Tests"], "windows": True},
        {"id": "vector", "paths": ["LiteDB/*Vector*"],
         "tests": ["LiteDB.Tests.Engine.Issue2881_VectorFormat_Tests", "LiteDB.Tests.QueryTest.VectorIndex_Tests"]},
    ],
    "windows_pattern": r"\b(?:DllImport|LibraryImport|OperatingSystem|RuntimeInformation|OSPlatform|Mutex|Registry)\b|#\s*(?:if|elif).*WINDOWS",
    "runtime_pattern": r"#\s*(?:if|elif).*NET|\b(?:Vector128|Vector256|Vector512|RuntimeFeature|RuntimeInformation)\b|\.(?:NetCore|NetStd)\.",
    "fallback": "six-os-framework-lanes-with-compatibility",
}
PLATFORMS = ("ubuntu-latest", "windows-latest", "macos-latest")
FRAMEWORKS = ("net8.0", "net10.0")
PREFIXES = {"ubuntu-latest": ("linux-x64",), "windows-latest": ("windows-x64",),
            "macos-latest": ("macos-x64", "macos-arm64")}


class ProfileError(ValueError):
    """A trusted, executable acceptance profile could not be established."""


def require(condition, message):
    if not condition:
        raise ProfileError(message)


def digest(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True, separators=(",", ":"),
                                     ensure_ascii=False).encode("utf-8")).hexdigest()


def require_sha(value):
    require(isinstance(value, str) and re.fullmatch(r"[0-9a-f]{40}", value),
            "Full lowercase immutable commit SHAs are required")


def lane_for_environment(environment):
    for os_name, prefixes in PREFIXES.items():
        for framework in FRAMEWORKS:
            if environment in {f"{prefix}-{framework}" for prefix in prefixes}:
                return os_name, framework
    raise ProfileError(f"Environment has no supported acceptance lane: {environment}")


def profile_for_changes(changes, contract, base_sha, candidate_sha, diff_text):
    """Pure planner; production callers must obtain changes/diff through plan()."""
    require_sha(base_sha)
    require_sha(candidate_sha)
    require(base_sha != candidate_sha, "Candidate must differ from its baseline")
    issue = contract.get("inventory_issue")
    require(type(issue) is int and issue > 0, "Contract lacks an exact issue identity")
    require(isinstance(changes, list) and changes and all(isinstance(path, str) for path in changes)
            and len(changes) == len(set(changes)),
            "A unique nonempty production path list is required")
    require(all(isinstance(path, str) and path.startswith("LiteDB/")
                and "\\" not in path and all(part not in ("", ".", "..") for part in path.split("/"))
                for path in changes), "Invalid production path")
    require(isinstance(diff_text, str) and diff_text.strip(), "Exact production diff is required")
    allowed = contract.get("allowed_production_paths")
    require(isinstance(allowed, list) and allowed and all(isinstance(path, str) for path in allowed)
            and set(changes) <= set(allowed), "Production diff exceeds the approved issue scope")
    environments = contract.get("environments")
    require(isinstance(environments, list) and environments
            and all(isinstance(env, str) for env in environments), "Missing approved environments")
    available = {lane_for_environment(env) for env in environments}
    required = contract.get("required_environments", [])
    require(isinstance(required, list) and all(env in environments for env in required),
            "Required environments must be explicitly approved")
    require(not any(env.startswith("macos-") for env in required),
            "An explicit macOS architecture requires a dedicated runner; macos-latest cannot promise it")
    first = next((lane for lane in ((os_name, framework) for framework in FRAMEWORKS
                                   for os_name in PLATFORMS) if lane in available), None)
    lanes = {first} | {lane_for_environment(env) for env in required}
    rules, filters = [], set()
    compatibility, unknown = False, False
    ordinary = POLICY["ordinary_pairs"].get(str(issue), [])
    compatible = POLICY["compatibility_pairs"].get(str(issue), {})
    for path in sorted(changes):
        matches = [rule for rule in POLICY["rules"]
                   if any(fnmatch.fnmatchcase(path, pattern) for pattern in rule["paths"])]
        if path in ordinary:
            rules.append({"rule": "reviewed-ordinary-issue-path", "path": path})
        elif path in compatible.get("paths", []):
            compatibility = True
            rules.append({"rule": "reviewed-compatible-issue-path", "path": path})
            filters.update("FullyQualifiedName~" + name for name in compatible["tests"])
        elif not matches:
            unknown = True
            rules.append({"rule": "unknown-production-path", "path": path})
        for rule in matches:
            compatibility = True
            rules.append({"rule": rule["id"], "path": path})
            filters.update("FullyQualifiedName~" + name for name in rule["tests"])
            if rule.get("windows"):
                lanes.add(("windows-latest", "net8.0"))
    sensitivity = "\n".join(sorted(changes)) + "\n" + diff_text
    if re.search(POLICY["windows_pattern"], sensitivity):
        lanes.add(("windows-latest", "net8.0"))
        rules.append({"rule": "platform-sensitive-diff", "path": "<production-diff>"})
    if re.search(POLICY["runtime_pattern"], sensitivity):
        lanes.update((os_name, "net10.0") for os_name, _ in list(lanes))
        rules.append({"rule": "runtime-sensitive-diff", "path": "<production-diff>"})
    if unknown:
        lanes = {(os_name, framework) for os_name in PLATFORMS for framework in FRAMEWORKS}
        compatibility = True
    require(lanes <= available, "Selected lanes exceed the reviewed environment contract")
    matrix = [{"os": os_name, "framework": framework} for os_name in PLATFORMS
              for framework in FRAMEWORKS if (os_name, framework) in lanes]
    planner_source = Path(__file__).read_bytes().replace(b"\r\n", b"\n")
    profile = {
        "schema_version": 1, "issue": issue, "base_sha": base_sha, "candidate_sha": candidate_sha,
        "paths": sorted(changes), "diff_sha256": hashlib.sha256(diff_text.replace("\r\n", "\n").encode()).hexdigest(),
        "contract_sha256": digest(contract), "policy_version": POLICY["version"],
        "policy_sha256": digest({"rules": POLICY, "planner_source_sha256": hashlib.sha256(planner_source).hexdigest()}),
        "required_lanes": [f"bugfix-check-{lane['os']}-{lane['framework']}" for lane in matrix],
        "matrix": matrix, "compatibility": compatibility, "production_build": True,
        "checks": ["focused", "broad", "production-build"],
        "targeted_test_filters": sorted(filters), "targeted_tests_covered_by_broad": True,
        "matched_rules": rules, "required_environments": sorted(required),
    }
    profile["profile_sha256"] = digest(profile)
    return profile


def git(repository, *arguments):
    result = subprocess.run(["git", "-C", str(repository), *arguments],
                            capture_output=True, check=True)
    return result.stdout.decode("utf-8")


def plan(repository, base_sha, candidate_sha, issue_contract):
    """Read immutable Git objects; working-tree edits cannot influence selection."""
    for sha in (base_sha, candidate_sha):
        require_sha(sha)
        require(git(repository, "rev-parse", "--verify", sha + "^{commit}").strip() == sha,
                "Source identity is not a commit")
    require(git(repository, "merge-base", base_sha, candidate_sha).strip() == base_sha,
            "Candidate must descend from its pinned integration base")
    paths = git(repository, "diff", "--name-only", "--no-renames", "-z",
                base_sha, candidate_sha, "--", "LiteDB/").rstrip("\0").split("\0")
    diff = git(repository, "-c", "core.quotePath=false", "diff", "--no-ext-diff", "--no-textconv",
               "--no-renames", "--no-color", "--no-relative", "--full-index", "--no-indent-heuristic",
               "--diff-algorithm=myers", "--src-prefix=a/", "--dst-prefix=b/", "--binary",
               "--unified=3", base_sha, candidate_sha, "--", "LiteDB/")
    return profile_for_changes(paths, issue_contract, base_sha, candidate_sha, diff)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repository", type=Path, required=True)
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--issue", type=int, required=True)
    parser.add_argument("--base-sha", required=True)
    parser.add_argument("--candidate-sha", required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args(argv)
    manifest = json.loads(args.manifest.read_text(encoding="utf-8"))
    require(manifest.get("schema_version") == 1, "Unsupported manifest schema")
    contract = manifest.get("issues", {}).get(str(args.issue))
    require(isinstance(contract, dict) and contract.get("inventory_issue") == args.issue,
            "Issue has no matching trusted execution contract")
    profile = plan(args.repository, args.base_sha, args.candidate_sha, contract)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(profile, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(profile))
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (ProfileError, OSError, ValueError, KeyError, subprocess.CalledProcessError) as error:
        print(f"Acceptance profile rejected: {error}", file=sys.stderr)
        sys.exit(1)
