"""Doc-claim lint: a normative documentation sentence must be anchored to its evidence.

A sentence in docs/**/*.md that promises behavior ("errors propagate", "never blocks",
"always", "rejects", "refuses", "guaranteed") is a contract. When nothing executes the
promise, code and documentation drift apart silently. Every added or changed sentence
containing one of those words must carry an anchor on the same sentence:

    [test: LiteDB.Tests/Engine/X_Tests.cs#Method]   or   [test: X_Tests#Method]
    [marker: refusal:pin-closed]

and the anchor must resolve: the test method exists, the marker is declared in
.github/safety/markers.json (when that registry exists; otherwise a warning).
Instead of an inline anchor the sentence may be registered as a claim in
.github/safety/contracts.json (check_contracts.py), with evidence whose `proves` says
how it establishes the sentence, or with an explicit gap. An anchor proves only that
a reference exists; the semantic review of a critical change judges whether it
establishes the sentence (docs/rules/safety-evidence.md#semantic-review).
Diff mode judges only sentences that are new or changed since --base, so the existing
backlog does not block unrelated changes; --all lists the backlog without failing.
"""
import argparse
import re
import sys

import check_contracts
import safety_common as common

DOCS = re.compile(r"docs/.+\.md\Z")
TRIGGERS = re.compile(r"\bpropagate[sd]?\b|\bnever\b|\balways\b|\breject(?:s|ed)?\b|\brefuse[sd]?\b|\bguaranteed\b",
                      re.I)


def claims(text):
    """Yield (first line, last line, sentence) for sentences that make a normative claim."""
    for first, last, sentence in common.doc_sentences(text):
        prose = re.sub(r"\]\([^)]*\)", "]", re.sub(r"`[^`]*`", "", common.ANCHOR.sub("", sentence)))
        if TRIGGERS.search(prose):
            yield first, last, sentence


def judge(tree, path, first, sentence, report, backlog=False, registered=()):
    """Report a claim without a resolving anchor or contracts.json claim; return True when it is unanchored."""
    for contract, claim in registered:
        if check_contracts.claim_matches(claim, path, sentence):
            if claim.get("gap"):
                report.warning(f"Claim registered in contracts.json '{contract}' as a gap: \"{sentence}\"", path, first)
            return False
    verdicts = [(anchor, verdict) for anchor, verdict in common.resolve_references(tree, sentence)
                if anchor.startswith("[")]
    log = report.warning if backlog else report.error
    if not verdicts:
        log(f"Normative sentence without a [test: ...] or [marker: ...] anchor or a contracts.json claim: "
            f"\"{sentence}\"", path, first)
        return True
    for anchor, verdict in verdicts:
        if verdict is None:
            report.warning(f"{anchor} cannot be checked: {common.SAFETY_DIR}/markers.json does not exist", path, first)
        elif not verdict:
            log(f"Anchor {anchor} does not resolve: \"{sentence}\"", path, first)
    return not any(verdict is not False for _, verdict in verdicts)


def check(base, head, report, everything=False):
    tree, old, flagged, judged = common.Tree(head), common.Tree(base or head), 0, 0
    registered = check_contracts.claims(check_contracts.load(tree, report)) if tree.exists(check_contracts.INDEX) else []
    added = {}
    if not everything:
        for path, line, _ in common.added_lines(base, head, ["docs/"]):
            added.setdefault(path, set()).add(line)
    for path in (p for p in (tree.paths() if everything else sorted(added)) if DOCS.match(p)):
        before = {common.normalize_prose(s) for _, _, s in common.doc_sentences(old.read(path) or "")}
        for first, last, sentence in claims(tree.read(path) or ""):
            if everything or (set(range(first, last + 1)) & added[path]
                              and common.normalize_prose(sentence) not in before):
                judged += 1
                flagged += judge(tree, path, first, sentence, report, everything, registered)
    tree.close()
    old.close()
    return judged, flagged


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--base", help="Base revision; only new or changed sentences are judged")
    parser.add_argument("--head", default="HEAD", help=f"Head revision or {common.WORKTREE}")
    parser.add_argument("--all", action="store_true", help="List every unanchored claim (backlog) without failing")
    common.add_mode_arguments(parser)
    args = parser.parse_args(argv)
    if not args.all and not args.base:
        parser.error("--base is required unless --all is given")
    report = common.Report("Doc-claim lint" + (" (backlog)" if args.all else ""))
    judged, flagged = check(args.base, args.head, report, everything=args.all)
    report.section(f"{judged} normative sentence(s) judged, {flagged} without a resolving anchor.")
    code = report.finish(advisory=args.all or common.net_advisory("lint-doc-claims", args.blocking))
    return code


if __name__ == "__main__":
    sys.exit(main())
