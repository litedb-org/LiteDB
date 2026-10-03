"""Safety-contract index: connect a few high-value invariants to executable evidence.

Validates .github/safety/contracts.json (every doc anchor, test, fuzz target and
script it names must exist, fuzz targets must be scheduled in CI and scripts must
be invoked by a workflow) and, given a base revision, reports which contracts the
changed paths implicate. A contract's `claims` map normative documentation sentences
to the evidence that establishes them or to an explicit gap; lint_doc_claims.py
accepts a claim registered here in place of an inline anchor, so there is one ledger. Path matching suggests obligations; it never proves a
change is safe, and a transitive storage/concurrency effect still needs review.
"""
import argparse
import json
import sys

import safety_common as common

INDEX = f"{common.SAFETY_DIR}/contracts.json"
EVIDENCE_KINDS = ("test", "fuzz", "script")


def load(tree, report):
    """The index with well-typed sections; malformed parts are reported and dropped."""
    try:
        index = tree.read_json(INDEX)
    except common.MalformedJson as error:
        report.error(str(error), INDEX)
        index = {}
    if index is None:
        report.error(f"{INDEX} is missing")
        index = {}
    contracts = common.section(index, "contracts", list, report, INDEX)
    for contract in contracts:
        if not isinstance(contract.get("id"), str):
            contract["id"] = None
        contract["evidence"] = common.section(contract, "evidence", list, report, INDEX)
        contract["claims"] = common.section(contract, "claims", list, report, INDEX)
        for key, kinds in (("docs", str), ("paths", str), ("gaps", (str, dict))):
            values = contract.get(key) or []
            contract[key] = [value for value in values if isinstance(value, kinds)] if isinstance(values, list) else []
    return {"contracts": contracts, "models": common.section(index, "models", dict, report, INDEX)}


def validate(tree, index, report):
    models = set(index.get("models", {}))
    targets = common.fuzz_targets(tree)
    scheduled = common.scheduled_fuzz_targets(tree)
    workflows = common.workflow_text(tree)
    anchors = {}
    ids = [contract.get("id") for contract in index.get("contracts", [])]
    for duplicate in sorted({value for value in ids if ids.count(value) > 1}):
        report.error(f"Contract id {duplicate} is used twice", INDEX)
    for contract in index.get("contracts", []):
        label = contract.get("id") or "<missing id>"
        if not contract.get("id") or not contract.get("title") or not contract.get("paths"):
            report.error(f"Contract {label}: needs id, title and paths", INDEX)
        if not contract.get("evidence"):
            report.error(f"Contract {label}: needs at least one evidence item", INDEX)
        for doc in contract.get("docs", []):
            _check_doc(tree, label, doc, anchors, report)
        for item in contract.get("evidence", []):
            _check_evidence(tree, label, item, models, targets, scheduled, workflows, report)
        for claim in contract.get("claims", []):
            _check_claim(tree, label, claim, models, targets, scheduled, workflows, report)
        for gap in contract.get("gaps", []):
            if len(str(gap.get("text") if isinstance(gap, dict) else gap).strip()) < 10:
                report.error(f"Contract {label}: describe each gap", INDEX)


def _check_doc(tree, label, doc, anchors, report):
    path, _, anchor = doc.partition("#")
    text = tree.read(path)
    if text is None:
        report.error(f"Contract {label}: document {path} does not exist", INDEX)
        return
    if anchor:
        known = anchors.setdefault(path, common.markdown_anchors(text))
        if anchor not in known:
            report.error(f"Contract {label}: {path} has no heading anchor #{anchor}", INDEX)


def claims(index):
    """Every (contract id, claim) of the index."""
    return [(contract.get("id"), claim) for contract in index.get("contracts", []) for claim in contract.get("claims", [])]


def claim_matches(claim, path, sentence):
    """True when the claim names this document and quotes (part of) this sentence."""
    quoted = common.normalize_prose(str(claim.get("sentence") or ""))
    return claim.get("doc") == path and bool(quoted) and quoted in common.normalize_prose(sentence)


def _check_claim(tree, label, claim, models, targets, scheduled, workflows, report):
    """A claim: {doc, sentence, evidence: [evidence items whose 'proves' says how they establish it]} or {doc,
    sentence, gap}. The sentence must still be in the document, so an edited promise is reviewed again."""
    where = f"Contract {label}: claim {claim.get('sentence')!r}"
    text = tree.read(str(claim.get("doc") or ""))
    if text is None:
        report.error(f"{where}: document {claim.get('doc')!r} does not exist", INDEX)
    elif not any(claim_matches(claim, claim["doc"], sentence) for _, _, sentence in common.doc_sentences(text)):
        report.error(f"{where}: no sentence of {claim['doc']} contains it any more; review the claim", INDEX)
    evidence, gap = claim.get("evidence"), str(claim.get("gap") or "").strip()
    if bool(evidence) == bool(gap) or (gap and len(gap) < 10) or (evidence and not isinstance(evidence, list)):
        report.error(f"{where}: give either evidence items or a gap of at least a sentence", INDEX)
        return
    for item in evidence or []:
        if isinstance(item, dict):
            _check_evidence(tree, label, item, models, targets, scheduled, workflows, report)
        else:
            report.error(f"{where}: each evidence item is an object", INDEX)


def _check_evidence(tree, label, item, models, targets, scheduled, workflows, report):
    kinds = [kind for kind in EVIDENCE_KINDS if item.get(kind)]
    if len(kinds) != 1:
        report.error(f"Contract {label}: each evidence item names exactly one of {', '.join(EVIDENCE_KINDS)}", INDEX)
        return
    if item.get("model") is not None and item["model"] not in models:
        report.error(f"Contract {label}: unknown failure model {item['model']!r}", INDEX)
    if len((item.get("proves") or "").strip()) < 10:
        report.error(f"Contract {label}: say what {item[kinds[0]]} proves (the observed event, not the name)", INDEX)
    if kinds == ["test"] and common.resolve_test(tree, item["test"]) is None:
        report.error(f"Contract {label}: test {item['test']} does not resolve", INDEX)
    elif kinds == ["fuzz"]:
        if item["fuzz"] not in targets:
            report.error(f"Contract {label}: fuzz target {item['fuzz']} does not exist", INDEX)
        elif item["fuzz"] not in scheduled:
            report.error(f"Contract {label}: fuzz target {item['fuzz']} is not run by {common.FUZZ_WORKFLOW}", INDEX)
    elif kinds == ["script"]:
        if tree.read(item["script"]) is None:
            report.error(f"Contract {label}: script {item['script']} does not exist", INDEX)
        elif item["script"] not in workflows:
            report.error(f"Contract {label}: script {item['script']} is not run by any workflow", INDEX)


def implicated(index, paths):
    """Return [(contract, [matching paths])] for contracts whose globs match the paths."""
    result = []
    for contract in index.get("contracts", []):
        if not contract.get("id"):
            continue
        patterns = [common.glob_regex(pattern) for pattern in contract.get("paths", [])]
        matches = sorted(path for path in paths if any(pattern.match(path) for pattern in patterns))
        if matches:
            result.append((contract, matches))
    return result


def describe(contract, matches):
    evidence = "; ".join(
        f"{item.get('test', '').split('#')[-1] or item.get('fuzz') or item.get('script')}"
        + (f" ({item['model']})" if item.get("model") else "") for item in contract.get("evidence", []))
    gaps = "; ".join(gap.get("text") if isinstance(gap, dict) else gap for gap in contract.get("gaps", []))
    shown = ", ".join(f"`{path}`" for path in matches[:5]) + (f" and {len(matches) - 5} more" if len(matches) > 5 else "")
    lines = [f"### `{contract['id']}`: {contract['title']}", f"Touched: {shown}", f"Evidence: {evidence}"]
    if gaps:
        lines.append(f"Known gaps: {gaps}")
    return "\n\n".join(lines)


def model_matrix(index):
    models = list(index.get("models", {}))
    rows = ["| Contract | " + " | ".join(models) + " |", "| --- |" + " --- |" * len(models)]
    for contract in index.get("contracts", []):
        present = {item.get("model") for item in contract.get("evidence", [])}
        rows.append(f"| `{contract['id']}` | " + " | ".join("yes" if model in present else "-" for model in models) + " |")
    return "\n".join(rows)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--base", help="Report the contracts implicated by changes since this revision")
    parser.add_argument("--head", default="HEAD")
    parser.add_argument("--implicated-output", help="Write the implicated contract ids as JSON to this file")
    args = parser.parse_args(argv)
    head = common.Tree(args.head)
    report = common.Report("Safety contracts")
    index = load(head, report)
    validate(head, index, report)
    found = implicated(index, common.changed_files(args.base, args.head)) if args.base else []
    if args.implicated_output:
        with open(args.implicated_output, "w", encoding="utf-8") as handle:
            json.dump([contract["id"] for contract, _ in found], handle)
    if args.base:
        report.section("Implicated contracts: name each id in the PR's safety section with its evidence, "
                       "or explain why the change cannot affect it.\n\n"
                       + ("\n\n".join(describe(contract, matches) for contract, matches in found)
                          if found else "None of the changed paths map to a contract."))
    report.section("Evidence by failure model (a `-` is an unclaimed model, not a pass):\n\n" + model_matrix(index))
    return report.finish()


if __name__ == "__main__":
    sys.exit(main())
