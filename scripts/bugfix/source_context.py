"""Exact audited source observation for issue 2871; never behavioral fix evidence."""

import hashlib
import json
import re
import subprocess

FROZEN = "dd937719f7eee53c512f50ac604cab639bf42a4c"
CLASS = "LiteDB.Tests.Audit2026.AuditFindingSourceGuard_Tests"
TEST = (CLASS + '.Audited_source_context_must_be_replaced(id: 132, file: '
        '"LiteDB/Client/Mapper/Reflection/Reflection.cs", line: 39, title: '
        '"Reflection.CreateInstance reads the shared ctor ca"···, context: '
        '"            {\\n                if (_cacheCtor.TryG"···)')
DECLARATION = {
    "guard_id": 132, "test_name": TEST,
    "source_path": "LiteDB/Client/Mapper/Reflection/Reflection.cs",
    "frozen_source_blob": "fd66327f298d785da70776bcc8623d061c0f147b",
    "fixture_blobs": {
        "LiteDB.Tests/Audit2026/AuditFindingSourceGuard_Tests.cs": "ba8272e35980a36ced33dcb4d61661b0061ebeb0",
        "LiteDB.Tests/Audit2026/audit-source-guards.json": "9f1b6689cb13fe3df06663a8f47fa17d96d0c986",
    },
    "context_sha256": "316e6638a079552c0284ddff9221ad696da899ed86188a00143ffee14c3a368a",
}


def require(condition, message):
    if not condition:
        raise ValueError(message)


def git(repository, *arguments):
    return subprocess.run(["git", "-C", str(repository), *arguments], capture_output=True, check=True).stdout


def blob(repository, revision, path):
    require(isinstance(revision, str) and re.fullmatch(r"[0-9a-f]{40}", revision), "Source observation requires immutable commits")
    entry = git(repository, "ls-tree", revision, "--", path).decode("utf-8").split()
    require(len(entry) >= 4 and entry[0] == "100644" and entry[1] == "blob", "Source observation requires ordinary Git blobs")
    return entry[2], git(repository, "show", f"{revision}:{path}")


def declaration(contract):
    declared = contract.get("source_context_observations", [])
    if not declared:
        return None
    require(contract.get("inventory_issue") == 2871 and contract.get("frozen_test_revision") == FROZEN
            and declared == [DECLARATION], "Unapproved source-context disposition")
    require(contract.get("allowed_production_paths") == [DECLARATION["source_path"]], "Source observation scope changed")
    targets = contract["regressions"] + contract["controls"]
    require(all(case["name"] != TEST for case in targets), "Source-only guard cannot receive behavioral issue credit")
    return DECLARATION


def observe(repository, contract, base_sha, candidate_sha):
    """Prove the frozen context disappeared in this exact approved production diff."""
    rule = declaration(contract)
    if rule is None:
        return []
    require(repository is not None, "Declared source observation requires an actual Git repository")
    require(git(repository, "merge-base", base_sha, candidate_sha).decode().strip() == base_sha,
            "Source observation candidate does not descend from its base")
    for path, expected in rule["fixture_blobs"].items():
        for revision in (FROZEN, base_sha, candidate_sha):
            require(blob(repository, revision, path)[0] == expected, "Frozen source-guard fixture changed")
    rows = json.loads(blob(repository, FROZEN, "LiteDB.Tests/Audit2026/audit-source-guards.json")[1])
    rows = [row for row in rows if row["id"] == 132]
    require(len(rows) == 1 and rows[0]["file"] == rule["source_path"], "Frozen source-guard row changed")
    row = rows[0]
    context = row["context"].replace("\r\n", "\n")
    require(hashlib.sha256(context.encode("utf-8")).hexdigest() == rule["context_sha256"], "Frozen source context changed")
    old_blob, before = blob(repository, base_sha, rule["source_path"])
    new_blob, after = blob(repository, candidate_sha, rule["source_path"])
    require(blob(repository, FROZEN, rule["source_path"])[0] == rule["frozen_source_blob"] == old_blob,
            "Source observation baseline blob changed")
    before, after = (raw.decode("utf-8").replace("\r\n", "\n") for raw in (before, after))
    require(before.count(context) == 1, "Audited context is not present exactly once in baseline")
    if context in after:
        return []
    changed = git(repository, "diff", "--name-status", "--no-renames", base_sha, candidate_sha).decode("utf-8").splitlines()
    require(changed == ["M\t" + rule["source_path"]] and new_blob != old_blob, "Source observation exceeds the exact approved diff")
    return [{"schema_version": 1, "issue": 2871, **rule, "frozen_test_revision": FROZEN,
             "base_sha": base_sha, "candidate_sha": candidate_sha, "base_source_blob": old_blob,
             "candidate_source_blob": new_blob, "baseline_context_count": 1, "candidate_context_count": 0,
             "baseline_failure": "Expected source.Contains(normalizedContext) to be False because finding 132 is still present at "
                 + f"{row['file']}:{row['line']}: {row['title']}, but found True.",
             "behavior_unverified": True, "issue_credit": False}]


def bind_observations(contract, provenance, observations):
    if not observations:
        return
    require(declaration(contract) is not None and provenance.get("issue") == 2871 and len(observations) == 1,
            "Source observation belongs to another issue")
    require(all(observations[0].get(key) == provenance.get(key) for key in ("base_sha", "candidate_sha")),
            "Source observation has stale source provenance")


def consume(previous, current, observations):
    """Only the authenticated guard-132 Failed-to-Passed pair is nonblocking."""
    matching = [item for item in observations if item.get("test_name") == current["name"]]
    if not matching:
        return None
    require(len(matching) == 1, "Ambiguous source observation")
    item = matching[0]
    require(item.get("guard_id") == 132 and item.get("issue") == 2871 and item.get("test_name") == TEST
            and item.get("behavior_unverified") is True and item.get("issue_credit") is False,
            "Invalid source-only disposition")
    require(previous["class_name"] == current["class_name"] == CLASS
            and previous["outcome"].lower() == "failed" and current["outcome"].lower() == "passed"
            and previous["failure"] == item["baseline_failure"], "Source-guard transition differs from its exact baseline evidence")
    return item


def final_observations(repository, accepted, final_sha):
    """Reauthenticate recorded per-fix observations and their final source context."""
    result = []
    for value in accepted.values():
        entry, contract = value["entry"], value["contract"]
        observed = observe(repository, contract, entry["base_sha"], entry["candidate_sha"])
        require(entry.get("source_context_changes", []) == observed, "Accepted source observations changed or are missing")
        for item in observed:
            final_blob, raw = blob(repository, final_sha, item["source_path"])
            rows = json.loads(blob(repository, FROZEN, "LiteDB.Tests/Audit2026/audit-source-guards.json")[1])
            context = next(row["context"] for row in rows if row["id"] == item["guard_id"])
            require(context not in raw.decode("utf-8").replace("\r\n", "\n"), "Audited source context returned in final integration")
            result.append({**item, "final_integration_sha": final_sha, "final_source_blob": final_blob})
    return result
