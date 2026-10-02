"""Check a PR description's "Safety / regression evidence" section.

The section answers five short prompts (or gives one justified "Not applicable:"
line) and names every contract id that check_contracts.py implicates for the
changed paths, so "what could this break?" starts from a lookup instead of
recall. It checks presence and applicability, not whether the prose is true.

A critical change (the `critical` label, or a diff classify_critical.py classifies
as critical) also needs the "Critical change evidence" section, generated from
artifacts by render_critical_sections.py: Intended changes (every entry the change
adds to intended-changes.json), Markers declared (every marker the diff declares,
with its campaign hit count), Proofs (known-bad commit -> net -> seed; every proof
entry the change adds), Blast radius (widened contract -> caller -> disposition) and
Benchmark diff (or "No hot path touched: <reason>"). A subsection with nothing to
list says "None: <reason>" or "Not applicable: <reason>" instead; a reason cannot
replace an entry the artifacts require. Non-critical PRs keep the five prompts only.
"""
import argparse
import json
import re
import sys

import check_contracts
import check_intended_changes
import check_reachability
import classify_critical
import lint_invariant_comments
import safety_common as common

HEADING = re.compile(r"^#{2,3}\s*Safety\s*/\s*regression evidence\s*$", re.I | re.M)
PROMPTS = [
    "Contracts and risk",
    "Prior states and interactions",
    "Failure outcomes",
    "Evidence",
    "Coverage delta and residual concerns",
]
NOT_APPLICABLE = re.compile(r"^\s*[-*]?\s*\**Not applicable\**\s*:\s*(.{20,})$", re.I | re.M)
TEMPLATE = ".github/pull_request_template.md"
CRITICAL_HEADING = re.compile(r"^##\s*Critical change evidence\s*$", re.I | re.M)
SUBSECTIONS = ["Intended changes", "Markers declared", "Proofs", "Blast radius", "Benchmark diff"]
JUSTIFIED = re.compile(r"^\s*[-*]?\s*\**(?:Not applicable|None|No hot path touched)\**\s*:\s*(.{20,})$", re.I | re.M)
ARROW = re.compile(r"→|->")
COMMIT = re.compile(r"\b[0-9a-f]{7,40}\b")
NET_PROOFS = f"{common.SAFETY_DIR}/net-proofs.json"
REGRESSION_PROOFS = f"{common.SAFETY_DIR}/regression-proofs.json"


def section(body):
    match = HEADING.search(body)
    if not match:
        return None
    level = len(match.group(0)) - len(match.group(0).lstrip("#"))  # subsections belong to the section
    following = re.search(rf"^#{{1,{level}}}\s+\S", body[match.end():], re.M)
    end = match.end() + following.start() if following else len(body)
    return re.sub(r"<!--.*?-->", "", body[match.end():end], flags=re.S)


def answers(text):
    """Return {prompt: answer text} for '- **Prompt:** answer' bullets."""
    found = {}
    for prompt in PROMPTS:
        match = re.search(r"^\s*[-*]\s*\**" + re.escape(prompt) + r"\**\s*:\**\s*(.*?)(?=^\s*[-*]\s*\**[A-Z][^:\n]{2,60}:|\Z)",
                          text, re.M | re.S)
        found[prompt] = re.sub(r"\s+", " ", match.group(1)).strip() if match else None
    return found


def check(body, implicated_ids, report):
    text = section(body or "")
    if text is None:
        report.error(f"The PR description has no '## Safety / regression evidence' section; copy it from {TEMPLATE}")
        return
    not_applicable = NOT_APPLICABLE.search(text)
    replies = answers(text)
    if not not_applicable:
        for prompt, reply in replies.items():
            if reply is None:
                report.error(f"Safety section: the '{prompt}' prompt is missing")
            elif len(reply) < 3:
                report.error(f"Safety section: answer '{prompt}' (or justify 'Not applicable:')")
    for contract in implicated_ids:
        if not re.search(r"(?<![\w-])" + re.escape(contract) + r"(?![\w-])", text):
            report.error(f"Safety section: the changed paths implicate contract '{contract}'; name it with its "
                         "evidence or explain why this change cannot affect it")


# --- critical changes -------------------------------------------------------------

def critical_subsections(body):
    """{subsection: text without comments} of '## Critical change evidence', or None without it."""
    match = CRITICAL_HEADING.search(body or "")
    if not match:
        return None
    following = re.search(r"^#{1,2}\s+\S", body[match.end():], re.M)
    text = body[match.end():match.end() + following.start()] if following else body[match.end():]
    text = re.sub(r"<!--.*?-->", "", text, flags=re.S)
    parts = {}
    for name in SUBSECTIONS:
        found = re.search(rf"^###\s*{re.escape(name)}\s*$(.*?)(?=^###?\s|\Z)", text, re.M | re.S | re.I)
        parts[name] = found.group(1).strip() if found else None
    return parts


def added_ids(base_tree, head_tree, path, key):
    def ids(tree):
        try:
            data = tree.read_json(path, {}) or {}
        except common.MalformedJson:
            return set()
        return {str(item.get(key)) for item in data.get("proofs", []) if isinstance(item, dict) and item.get(key)}
    return sorted(ids(head_tree) - ids(base_tree))


def critical_context(base, head):
    """What the artifacts require the critical section to list for base..head."""
    base_tree, head_tree = common.Tree(base), common.Tree(head)
    quiet = check_reachability._Quiet("context")
    try:
        markers = check_reachability.all_markers(head_tree, quiet)
        declared = check_reachability.declared_for_diff(base, head_tree, markers, quiet)
    except common.MalformedJson:
        markers, declared = {}, {}
    return {
        "manifest": check_intended_changes.added_entries(base_tree, head_tree),
        "markers": {name: {"reasons": reasons, "gate": markers.get(name, {}).get("gate", "smoke")}
                    for name, reasons in sorted(declared.items())},
        "proofs": added_ids(base_tree, head_tree, NET_PROOFS, "id")
                  + added_ids(base_tree, head_tree, REGRESSION_PROOFS, "repro"),
    }


def check_critical(body, context, report, rules=()):
    parts = critical_subsections(body)
    if parts is None:
        report.error("This change is critical: add the '## Critical change evidence' section from the template "
                     "(render it with .github/scripts/render_critical_sections.py)")
        return
    for name, text in parts.items():
        if text is None:
            report.error(f"Critical section: the '### {name}' subsection is missing")
    lines = {name: (text or "").splitlines() for name, text in parts.items()}
    justified = {name: bool(text and JUSTIFIED.search(text)) for name, text in parts.items()}

    manifest = context.get("manifest", [])
    listed = 0
    for entry in manifest:
        if any(entry.get("call", "\0") in line and entry.get("change", "\0") in line
               for line in lines["Intended changes"]):
            listed += 1
        else:
            report.error(f"Critical section: list the intended change {entry.get('call')}/{entry.get('change')} "
                         "this change adds to intended-changes.json")
    _rows_or_reason("Intended changes", bool(manifest), listed > 0, justified, parts, report)

    listed = 0
    for name, marker in context.get("markers", {}).items():
        line = next((line for line in lines["Markers declared"] if name in line), None)
        count = re.search(re.escape(name) + r"\D*?(\d+)", line) if line else None
        if count is None:
            report.error(f"Critical section: marker {name} is declared by this diff; list it with its campaign "
                         "hit count (reachability.json)")
            continue
        listed += 1
        if int(count.group(1)) == 0 and marker.get("gate") != "advisory":
            report.error(f"Critical section: marker {name} was never hit; extend a fuzz target so it is reached")
    _rows_or_reason("Markers declared", bool(context.get("markers")), listed > 0, justified, parts, report)

    for proof in context.get("proofs", []):
        if not any(proof in line for line in lines["Proofs"]):
            report.error(f"Critical section: name the proof entry {proof} this change adds")
    rows = [line for line in lines["Proofs"] if ARROW.search(line)]
    for line in rows:
        if not COMMIT.search(line):
            report.error(f"Critical section: a proof row names its known-bad commit: {line.strip()[:80]}")
    _rows_or_reason("Proofs", bool(context.get("proofs")), bool(rows), justified, parts, report)

    rows = [line for line in lines["Blast radius"] if ARROW.search(line)]
    _rows_or_reason("Blast radius", False, bool(rows), justified, parts, report)

    benchmark = parts.get("Benchmark diff") or ""
    measured = bool(re.search(r"\d|https?://", JUSTIFIED.sub("", benchmark)))
    _rows_or_reason("Benchmark diff", False, measured, justified, parts, report)
    if justified["Benchmark diff"] and not measured and "obligation:lock-wait" in rules:
        report.warning("Critical section: the diff changes a wait or lock (obligation lock-wait); 'No hot path "
                       "touched' needs a reason a reviewer can check (the contention benchmark is an advisory pilot)")


def _rows_or_reason(name, required, has_rows, justified, parts, report):
    """Rows the artifacts require cannot be replaced by a reason; with nothing required, rows or a reason."""
    if parts.get(name) is None:
        return
    if required and justified[name] and not has_rows:
        report.error(f"Critical section: '{name}' cannot be 'None': the artifacts require entries")
    elif not required and not has_rows and not justified[name]:
        report.error(f"Critical section: '{name}' needs its rows from the artifacts, or 'None: <reason>' "
                     "(20+ characters)")


def is_critical(base, head, labels, report):
    """(critical, classification rules) from the PR's labels and the diff."""
    result = classify_critical.evaluate(base, head, report=check_reachability._Quiet("classify"))
    rules = sorted({item["rule"] for item in result["reasons"]})
    critical = classify_critical.LABEL in labels or result["critical"]
    if critical:
        why = "label" if classify_critical.LABEL in labels else "diff"
        report.section(f"Critical change ({why}); rules matched: " + (", ".join(f"`{r}`" for r in rules) or "none"))
    return critical, rules


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--body", required=True, help="File holding the PR description")
    parser.add_argument("--base", required=True, help="PR base revision")
    parser.add_argument("--head", default="HEAD")
    parser.add_argument("--labels", default="[]", help="The PR's labels as a JSON array of names")
    args = parser.parse_args(argv)
    with open(args.body, encoding="utf-8-sig") as handle:
        body = handle.read()
    head = common.Tree(args.head)
    report = common.Report("PR safety section")
    index = check_contracts.load(head, report)
    found = check_contracts.implicated(index, common.changed_files(args.base, args.head))
    check(body, [contract["id"] for contract, _ in found], report)
    lint_invariant_comments.check_body(body, lint_invariant_comments.deletions(args.base, args.head), args.head,
                                       report, advisory=common.net_advisory("lint-invariant-comments"))
    labels = json.loads(args.labels or "[]")
    critical, rules = is_critical(args.base, args.head, labels if isinstance(labels, list) else [], report)
    if critical:
        check_critical(body, critical_context(args.base, args.head), report, rules)
    report.section("Implicated contracts: " + (", ".join(f"`{contract['id']}`" for contract, _ in found) or "none"))
    return report.finish()


if __name__ == "__main__":
    sys.exit(main())
