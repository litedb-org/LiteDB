"""Review completion policy: cosmetic nits alone do not require another patch."""

SEVERITIES = {"nit", "minor", "major", "critical"}


def only_nits(findings):
    """Missing or unknown severity never grants a nonblocking disposition."""
    return isinstance(findings, list) and all(
        isinstance(item, dict) and set(item) == {"severity", "summary", "path", "evidence"}
        and item.get("severity") == "nit"
        and all(isinstance(item.get(key), str) and item[key].strip()
                for key in ("summary", "path", "evidence"))
        for item in findings
    )


def validate_review(verdict, findings):
    if verdict not in {"pass", "changes_requested", "inconclusive"} or not isinstance(findings, list):
        raise ValueError("Invalid review verdict or findings")
    for finding in findings:
        if not isinstance(finding, dict) or set(finding) != {"summary", "path", "evidence", "severity"}:
            raise ValueError("Each finding needs summary, path, evidence, and severity")
        if not all(isinstance(value, str) and value.strip() for value in finding.values()):
            raise ValueError("Finding evidence cannot be empty")
        if finding["severity"] not in SEVERITIES:
            raise ValueError("Unknown finding severity")
    if verdict == "pass" and not only_nits(findings):
        raise ValueError("Verdict and findings disagree: blocking finding cannot pass")
    if verdict == "changes_requested" and only_nits(findings):
        raise ValueError("Verdict and findings disagree: nit-only review must pass")
    if verdict == "inconclusive" and not findings:
        raise ValueError("Inconclusive review must explain missing evidence")
