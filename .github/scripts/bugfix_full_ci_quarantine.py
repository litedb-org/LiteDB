"""Validate explicit, reviewed coverage gaps for the full-CI pilot."""

import hashlib
import json
from pathlib import Path


class QuarantineError(ValueError):
    pass


def load_quarantine(path):
    raw = Path(path).read_bytes()
    data = json.loads(raw)
    if data.get("schema_version") != 1:
        raise QuarantineError("Unsupported full-CI quarantine schema")
    original = data.get("expected_original_jobs")
    remaining = data.get("expected_remaining_jobs")
    entries = data.get("quarantines")
    if not isinstance(original, int) or not isinstance(remaining, int):
        raise QuarantineError("Quarantine job counts must be integers")
    if not isinstance(entries, list) or not entries:
        raise QuarantineError("At least one explicit quarantine is required")
    jobs = []
    for entry in entries:
        if (not isinstance(entry, dict) or entry.get("kind") != "repro"
                or entry.get("status") != "unverified"):
            raise QuarantineError("Only explicitly unverified repros may be quarantined")
        for field in ("issue", "repro", "authorized_on", "authorization", "reason"):
            if not entry.get(field):
                raise QuarantineError(f"Quarantine lacks {field}")
        excluded = entry.get("jobs")
        if not isinstance(excluded, list) or not excluded:
            raise QuarantineError("Quarantine lacks exact job identities")
        prefix = f"repro-runner / Run {entry['repro']} on "
        if not all(isinstance(job, str) and job.startswith(prefix) for job in excluded):
            raise QuarantineError("Quarantine contains a mismatched job identity")
        jobs.extend(excluded)
    if len(jobs) != len(set(jobs)) or original - remaining != len(jobs):
        raise QuarantineError("Quarantine counts do not match its exact excluded jobs")
    return {"sha256": hashlib.sha256(raw).hexdigest(), "jobs": set(jobs),
            "original_jobs": original, "remaining_jobs": remaining,
            "coverage_gaps": entries}
