"""Bound the structured diagnostics passed to a subsequent repair worker."""

import json


def repair_feedback(state):
    candidate = state["candidate_sha"]
    if candidate is None:
        return ""
    checks = [{"kind": event["kind"], "run_id": event.get("run_id"), "outcome": event.get("outcome"),
               "diagnostics": event.get("diagnostics", []), "reason": event.get("reason", "")}
              for event in state["history"] if event.get("candidate_sha") == candidate
              and event["kind"] in ("focused", "broad", "acceptance") and event.get("outcome") != "pass"]
    reviews = state["orchestration"].get("review_reports", {}).get(candidate, [])
    latest = {review["role"]: review for review in reviews if review.get("findings")}
    findings = [{"role": review["role"], "run_id": review["run_id"], "findings": review["findings"]}
                for review in latest.values()]
    payload = {"candidate_sha": candidate, "checks": checks[-3:], "reviews": findings,
               "review_instruction": "When any review finding exceeds nit severity, address every finding, including all nits."}
    encoded = json.dumps(payload, ensure_ascii=False)
    if len(encoded) > 23500:
        raise ValueError("Complete repair feedback exceeds dispatch limit; findings must not be truncated")
    return encoded
