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
    findings = [{"role": review["role"], "run_id": review["run_id"], "findings": review.get("findings", [])}
                for review in reviews if review.get("findings")]
    payload = {"candidate_sha": candidate, "checks": checks[-3:], "reviews": findings[-3:]}
    encoded = json.dumps(payload, ensure_ascii=False)
    if len(encoded) > 23500:
        payload = {"candidate_sha": candidate, "truncated": True, "diagnostic_excerpt": encoded[:10000]}
        encoded = json.dumps(payload, ensure_ascii=False)
    return encoded
