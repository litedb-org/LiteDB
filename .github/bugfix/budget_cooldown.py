"""Authenticate trusted gh-aw daily-budget decisions; never infer them from skips."""

from datetime import datetime, timezone
import hashlib
import json
import math
import time

from artifacts import download, read_members
from state import require


def notice(repo, workflow_run, artifacts, jobs):
    matching = [item for item in artifacts if item.get("name") == "bugfix-budget" and not item.get("expired", True)]
    if not matching:
        return None
    require(len(matching) == 1, "Ambiguous budget attestation")
    require(workflow_run.get("status") == "completed", "Budget workflow is incomplete")
    agent = [job for job in jobs if job.get("name") == "agent"]
    require(len(agent) == 1 and agent[0].get("conclusion") == "skipped", "Budget evidence requires skipped agent")
    conclusion = [job for job in jobs if job.get("name") == "conclusion"]
    require(len(conclusion) == 1 and conclusion[0].get("conclusion") == "success", "Budget attestation job did not succeed")
    raw = download(repo, matching[0])
    report_bytes = read_members(raw, ["budget-status.json"])["budget-status.json"]
    report = json.loads(report_bytes)
    base_fields = {"schema_version", "run_id", "run_attempt", "workflow_sha", "observed_at"}
    unavailable = report.get("status") == "accounting_unavailable"
    extra_fields = {"status"} if unavailable else {"exceeded", "threshold", "total"}
    require(set(report) == base_fields | extra_fields, "Invalid budget attestation fields")
    require(type(report["schema_version"]) is int and report["schema_version"] == 1,
            "Invalid budget attestation schema")
    if not unavailable:
        require(report["exceeded"] is True, "Budget guard was not activated")
    for field, expected in (("run_id", workflow_run["id"]), ("run_attempt", workflow_run["run_attempt"])):
        require(type(report[field]) is int and report[field] == expected, "Budget run identity mismatch")
    require(report["workflow_sha"] == workflow_run["head_sha"], "Budget workflow identity mismatch")
    if not unavailable:
        require(all(type(report[field]) in (int, float) and math.isfinite(report[field]) for field in ("threshold", "total"))
                and report["threshold"] > 0 and report["total"] >= report["threshold"], "Invalid positive budget decision")
    require(isinstance(report["observed_at"], str) and report["observed_at"].endswith("Z"), "Budget timestamp must be UTC")
    observed = datetime.fromisoformat(report["observed_at"].replace("Z", "+00:00")).timestamp()
    created = datetime.fromisoformat(workflow_run["created_at"].replace("Z", "+00:00")).timestamp()
    require(created <= observed <= time.time() + 300, "Budget timestamp does not belong to this run")
    return {"report": report, "report_sha256": hashlib.sha256(report_bytes).hexdigest(),
            "artifact_sha256": hashlib.sha256(raw).hexdigest(), "artifact_id": matching[0]["id"],
            "reason": "accounting_unavailable" if unavailable else "daily_credit_limit",
            "resume_after": observed + (15 * 60 if unavailable else 24 * 60 * 60)}
