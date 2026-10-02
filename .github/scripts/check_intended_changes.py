"""Intended-changes manifest: every behavior change a PR means to make, tied to its promise.

.github/safety/intended-changes.json is the contract ledger the differential run
(differential_run.py) reads: a difference between the merge-base and the head that no
entry covers fails the run, and an entry whose change is not observed fails it too.
Like coverage-ledger dispositions, only entries ADDED by the change count; entries
inherited from the base describe earlier PRs and are ignored.

    {"call": "Dispose", "dimension": "mode=shared", "change": "new-exception",
     "before": "none", "after": "System.IO.IOException",
     "doc": "docs/transaction-handles.md#cleanup failures now propagate",
     "reason": "Close errors were swallowed; callers could not see a lost checkpoint."}

`call` is the operation class as recorded in outcomes.jsonl (a marker name for
`marker`; for `cleanup-change` the op of the ConnectionClean/Quiescent/ScratchLive
evaluation); `dimension` is optional and may use `*` wildcards. Latency is not a
contract change: performance is separate evidence. `doc` is `docs/<file>.md#<fragment>`: the fragment is either text
quoted from the promising sentence, or a heading anchor whose section has a sentence
naming the call. This check validates shape and doc references; whether the change
happened is the differential run's verdict.
"""
import argparse
import fnmatch
import re
import sys

import safety_common as common

MANIFEST = f"{common.SAFETY_DIR}/intended-changes.json"
CHANGES = {"new-exception", "exception-removed", "primary-changed", "outcome-change", "payload-change",
           "effect-change", "cleanup-change", "marker"}


def entries(tree, report=None):
    """The manifest's entries (object form with 'changes', or a bare list)."""
    data = tree.read_json(MANIFEST, [])
    items = data.get("changes", []) if isinstance(data, dict) else data
    if not isinstance(items, list) or not all(isinstance(item, dict) for item in items):
        if report:
            report.error(f"{MANIFEST}: 'changes' must be a list of objects", MANIFEST)
        return []
    return items


def added_entries(base, head, report=None):
    """Entries present at head but not at base: the ones this change claims."""
    old = {_key(item) for item in entries(base)} if base else set()
    return [item for item in entries(head, report) if _key(item) not in old]


def _key(item):
    return repr(sorted((key, str(value)) for key, value in item.items()))


def covers(entry, change, op, dimension):
    pattern = entry.get("dimension")
    return (entry.get("change") == change and entry.get("call") == op
            and (not pattern or fnmatch.fnmatchcase(dimension or "", pattern)))


def resolve_doc(tree, reference, call=None):
    """Return the promising sentence a 'docs/x.md#fragment' reference names, or None.

    The fragment is quoted text of the sentence, or a heading anchor whose section has a
    sentence naming the call (its last dotted segment, case-insensitive).
    """
    path, _, fragment = (reference or "").partition("#")
    text = tree.read(path) if path.startswith("docs/") and path.endswith(".md") else None
    if text is None or not fragment.strip():
        return None
    wanted = common.normalize_prose(fragment)
    found = next((s for _, _, s in common.doc_sentences(text) if wanted in common.normalize_prose(s)), None)
    if found:
        return found
    headings = list(re.finditer(r"^(#{1,6})\s+.+$", text, re.M))
    short = (call or "").split(".")[-1].lower()
    for index, match in enumerate(headings):
        if short and common.markdown_anchors(match.group(0)) == {fragment}:
            end = next((m.start() for m in headings[index + 1:] if len(m.group(1)) <= len(match.group(1))), len(text))
            body = common.doc_sentences(text[match.end():end])
            return next((s for _, _, s in body if short in s.lower()), None)
    return None


def validate(tree, items, report):
    seen = set()
    for item in items:
        label = f"{item.get('call')}/{item.get('change')}"
        if item.get("change") not in CHANGES:
            report.error(f"{label}: change must be one of {', '.join(sorted(CHANGES))}", MANIFEST)
        for name in ("call", "before", "after", "doc"):
            if not isinstance(item.get(name), str) or not item[name].strip():
                report.error(f"{label}: '{name}' is required", MANIFEST)
        if len(str(item.get("reason") or "").strip()) < 10:
            report.error(f"{label}: explain the reason (at least a sentence)", MANIFEST)
        key = (item.get("call"), item.get("dimension"), item.get("change"))
        if key in seen:
            report.error(f"{label}: listed twice for the same dimension", MANIFEST)
        seen.add(key)
        if resolve_doc(tree, item.get("doc"), item.get("call")) is None:
            report.error(f"{label}: doc reference {item.get('doc')!r} does not resolve to a sentence "
                         "promising the change", MANIFEST)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--base", help="Only entries added since this revision are validated")
    parser.add_argument("--head", default="HEAD")
    common.add_mode_arguments(parser)
    args = parser.parse_args(argv)
    head = common.Tree(args.head)
    report = common.Report("Intended-changes manifest")
    try:
        items = added_entries(common.Tree(args.base) if args.base else None, head, report)
        validate(head, items, report)
    except common.MalformedJson as error:
        report.error(str(error), MANIFEST)
        items = []
    report.section("\n".join([f"{len(items)} intended change(s) claimed by this change."] + [
        f"- `{item.get('call')}` {item.get('dimension') or ''} {item.get('change')}: "
        f"{item.get('before')} → {item.get('after')} ({item.get('doc')})" for item in items]))
    return report.finish(advisory=common.net_advisory("intended-changes", args.blocking))


if __name__ == "__main__":
    sys.exit(main())
