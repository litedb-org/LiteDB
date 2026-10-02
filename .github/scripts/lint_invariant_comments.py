"""Invariant-comment lint: a deleted invariant comment must say where it is enforced now.

A comment that states an ordering or prohibition ("must not", "never", "invariant",
"do not", "must ... before/after", "cannot", "without") is often the only record of why code is shaped the
way it is. Deleting it lets the next change undo the shape without anyone noticing.
The lint finds such comment lines removed from LiteDB/ since --base. A comment whose
text reappears unchanged in the diff (moved or reflowed) is not a deletion. Every
other deletion must be listed in the PR description under `### Moved invariants`:

    - LiteDB/Engine/X.cs:120 → LiteDB.Tests/Engine/X_Tests.cs#Waits_In_Order
    - "readers must not outlive the pin" → [marker: refusal:pin-closed]

Left of the arrow: the base `path:line` (or file name and line) or a quote of at least
three words of the comment; right of it: at least one test, marker or code reference
that resolves at head. Without --pr-body-file the deletions are listed as warnings
(the Safety policy job); the PR safety-section workflow checks the description.
"""
import argparse
import re
import sys

import safety_common as common

SOURCE_ROOT = "LiteDB/"
# The trigger words, in one place. 'cannot' and 'without' were added after the designed list
# missed a deleted "Block without polling: ... cannot barge ahead" comment (see
# docs/rules/safety-evidence.md); over 300 dev commits they raise hits from 32 to 45.
TRIGGERS = re.compile(r"\bmust not\b|\bmustn't\b|\bnever\b|\binvariant\b|\bdo not\b|\bdon't\b"
                      r"|\bmust\b.*\b(?:before|after)\b|\bcannot\b|\bwithout\b", re.I)
SECTION = re.compile(r"^(#{2,4})\s*Moved invariants\s*$", re.I | re.M)


def deletions(base, head):
    """Return [(path, first base line, comment text)] for deleted invariant comments."""
    trees, cache = {"-": common.Tree(base), "+": common.Tree(head)}, {}

    def comments(sign, path):
        if (sign, path) not in cache:
            cache[sign, path] = common.comment_lines(trees[sign].read(path) or "")
        return cache[sign, path]

    removed, moved = {}, []
    for sign, path, line, raw in common.diff_lines(base, head, ["."]):
        if sign == "+":
            moved.append(comments(sign, path).get(line, "") if path.endswith(".cs") else raw)
        elif path.startswith(SOURCE_ROOT) and path.endswith(".cs") and comments(sign, path).get(line):
            blocks, text = removed.setdefault(path, []), comments(sign, path)[line]
            if blocks and blocks[-1][1] == line - 1:
                blocks[-1] = (blocks[-1][0], line, f"{blocks[-1][2]} {text}")
            else:
                blocks.append((line, line, text))
    corpus = common.normalize_prose(" ".join(moved))
    found = []
    for path, blocks in sorted(removed.items()):
        kept = common.normalize_prose(" ".join(comments("+", path).values()))
        found += [(path, first, text) for first, _, text in blocks
                  if TRIGGERS.search(text) and common.normalize_prose(text) not in f"{corpus} | {kept}"]
    for tree in trees.values():
        tree.close()
    return found


def check_body(body, found, head, report):
    """Every deletion needs a '### Moved invariants' line naming it and a resolving reference."""
    match = SECTION.search(body or "")
    following = match and re.search(rf"^#{{1,{len(match.group(1))}}}\s+\S", body[match.end():], re.M)
    text = body[match.end():match.end() + following.start()] if following else body[match.end():] if match else ""
    entries = [line for line in text.splitlines() if re.search(r"→|->", line)]
    tree = common.Tree(head)
    for path, line, comment in found:
        def names(entry, left):
            cited = re.search(r"([\w./-]+):(\d+)", left)
            if cited and path.endswith(cited.group(1).lstrip("./")) and abs(int(cited.group(2)) - line) <= 2:
                return True
            quotes = re.findall(r"[\"“'`](.+?)[\"”'`]", left)
            return any(len(quote.split()) >= 3 and common.normalize_prose(quote) in common.normalize_prose(comment)
                       for quote in quotes)
        entry = next((item for item in entries if names(item, re.split(r"→|->", item)[0])), None)
        if entry is None:
            report.error(f"Deleted invariant comment needs a '### Moved invariants' entry: \"{comment}\"", path, line)
        elif not any(verdict for _, verdict in common.resolve_references(tree, re.split(r"→|->", entry, 1)[1])):
            report.error(f"Moved invariant \"{comment}\": name a test, marker or code reference that resolves "
                         f"(got: {entry.strip()})", path, line)
    tree.close()


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--base", required=True, help="Base revision (PR base or merge-base)")
    parser.add_argument("--head", default="HEAD")
    parser.add_argument("--pr-body-file", help="PR description to check; without it deletions are only listed")
    args = parser.parse_args(argv)
    report = common.Report("Invariant-comment lint")
    found = deletions(args.base, args.head)
    if args.pr_body_file:
        with open(args.pr_body_file, encoding="utf-8-sig") as handle:
            check_body(handle.read(), found, args.head, report)
    else:
        for path, line, comment in found:
            report.warning(f"Deleted invariant comment; list it under '### Moved invariants' in the PR "
                           f"description with where it is enforced now: \"{comment}\"", path, line)
    report.section(f"{len(found)} deleted invariant comment(s)." if found else "No invariant comment deleted.")
    return report.finish()


if __name__ == "__main__":
    sys.exit(main())
