"""Check a PR description's "Safety / regression evidence" section.

The section answers five short prompts (or gives one justified "Not applicable:"
line) and names every contract id that check_contracts.py implicates for the
changed paths, so "what could this break?" starts from a lookup instead of
recall. It checks presence and applicability, not whether the prose is true.
"""
import argparse
import re
import sys

import check_contracts
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


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--body", required=True, help="File holding the PR description")
    parser.add_argument("--base", required=True, help="PR base revision")
    parser.add_argument("--head", default="HEAD")
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
    report.section("Implicated contracts: " + (", ".join(f"`{contract['id']}`" for contract, _ in found) or "none"))
    return report.finish()


if __name__ == "__main__":
    sys.exit(main())
