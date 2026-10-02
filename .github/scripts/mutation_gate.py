"""Mutation gate: surviving mutants on the changed lines of a diff, as lines to test.

Reads a Stryker.NET JSON report (mutation-testing-report-schema: files -> mutants
with a status and a start/end location) and keeps only the mutants whose span
touches a line added or modified between --base and --head (git diff -U0).
Every surviving mutant there (status Survived or NoCoverage) is listed with its
file:line, mutator and replacement. The list is a set of lines to write behavior
tests for, not a score: no percentage is computed or compared.

Each survivor is classified: class "blocking" when it sits in cleanup or lock code
(see BLOCKING_SCOPE), "advisory" otherwise. The class is information for the
reviewer. Whether a blocking-class survivor fails the job is decided by the single
switch in .github/safety/net-modes.json (net "mutation-gate"; --advisory/--blocking
override it locally): while the switch is off this is a pilot and the gate exits 0.
An unusable input (unreadable report, report source differing from --head, a
report file matching several changed files) fails in either mode.
Killed and Timeout mutants are detected (a Timeout is a mutant that made the
covering tests hang, which counts as detected, as in Stryker). CompileError and
RuntimeError mutants could not be evaluated (Stryker's safe mode turns every
mutant of a method into CompileError when one of them does not compile), and
Pending mutants were never run: these are counted and, in cleanup or lock code,
warned about (the changed line has no mutation evidence either way), but never
blocking. Ignored mutants were excluded on purpose (mutate scope, ignore-methods)
and are only counted.

Locations are mapped onto the C# source of the head revision (comments and
literals blanked with safety_common.blank_code, bodies found by brace matching),
and the report's embedded source must equal that revision, or the line numbers
could point at different code.
"""
import argparse
import json
import re
import sys
from pathlib import PurePosixPath

import safety_common as common

SURVIVING = ("Survived", "NoCoverage")
DETECTED = ("Killed", "Timeout")
UNEVALUATED = ("CompileError", "RuntimeError", "Pending")

# The one definition of "cleanup or lock code" for the gate. A mutant is blocking
# when its start lies
#  - inside the body of a method (or local function) whose name matches
#    CLEANUP_METHOD, or inside a finalizer (~Type());
#  - inside a `finally { }` block;
#  - inside a class/struct/record/interface, or in a file, whose name contains a
#    word matching LOCK_WORD. Names are split into words (PascalCase humps, dots,
#    underscores), so CollectionLock, TransactionGate, SharedMutexPin or
#    Snapshot.Lifetime.cs match while Block, Aggregate or Mapping do not.
CLEANUP_METHOD = re.compile(r"Dispose|DisposeAsync|Close|CloseAsync|Release\w*|\w*Finally")
LOCK_WORD = re.compile(r"lock(?:s|ed|ing|er)?|gate[sd]?|gating|monitor(?:s|ed|ing)?|mutex(?:es)?"
                       r"|pin(?:s|ned|ning)?|turnstiles?|lifetimes?", re.I)
BLOCKING_SCOPE = (
    "a mutant is blocking when it lies in the body of a method named Dispose, DisposeAsync, Close, "
    "CloseAsync, Release* or *Finally (or a finalizer), inside a finally block, or in a type or file whose "
    "name contains the word lock, gate, monitor, mutex, pin, turnstile or lifetime")

_TYPE = re.compile(r"\b(?:class|struct|record|interface)\s+(?P<name>[A-Za-z_]\w*)")
_FINALLY = re.compile(r"\bfinally\s*\{")
_CALLABLE = re.compile(r"(?P<tilde>~\s*)?\b(?P<name>[A-Za-z_]\w*)\s*(?:<[^<>(){};]*>\s*)?\(")
_NOT_DECLARATION = {
    "if", "for", "foreach", "while", "switch", "using", "lock", "catch", "fixed", "when", "return",
    "nameof", "typeof", "sizeof", "default", "checked", "unchecked", "base", "this", "new", "await",
    "throw", "in", "is", "and", "or", "not", "else", "do", "case", "yield", "stackalloc", "delegate"}
_AFTER_PARAMETERS = re.compile(r"\s*(?:where\s[^{;]*?)?(?:(?P<arrow>=>)|(?P<brace>\{)|:\s*(?:base|this)\s*\()")


def words(name):
    """Split an identifier or file stem into lower-case words."""
    return [word.lower() for part in re.split(r"[._\W]+", name)
            for word in re.findall(r"[A-Z]+(?=[A-Z][a-z])|[A-Z]?[a-z]+|[A-Z]+|\d+", part)]


def lock_word(name):
    return next((word for word in words(name) if LOCK_WORD.fullmatch(word)), None)


class Structure:
    """Spans of types, methods and finally blocks in one C# file (offsets into the source)."""

    def __init__(self, text):
        self.text = text
        code = common.blank_code(text)
        self.line_starts = [0] + [index + 1 for index, char in enumerate(text) if char == "\n"]
        self.types = [(match.group("name"),) + _block(code, match.end()) for match in _TYPE.finditer(code)]
        self.finally_blocks = [(match.end() - 1, common.matching(code, match.end() - 1, "{", "}"))
                               for match in _FINALLY.finditer(code)]
        self.methods = list(_methods(code))

    def offset(self, line, column):
        if line < 1 or line > len(self.line_starts):
            return None
        start = self.line_starts[line - 1]
        end = self.line_starts[line] - 1 if line < len(self.line_starts) else len(self.text)
        return min(start + max(column - 1, 0), end)

    def snippet(self, line, column, end_line, end_column):
        """The mutated source text (first line only for multi-line spans)."""
        start = self.offset(line, column)
        if start is None:
            return ""
        stop = self.offset(end_line, end_column) if end_line == line and isinstance(end_column, int) else None
        stop = self.text.find("\n", start) if stop is None else stop
        return self.text[start:stop if stop >= 0 else len(self.text)].strip()

    def classify(self, path, line, column):
        """Return (blocking, reason) for a mutant starting at line/column."""
        position = self.offset(line, column)
        if position is None:
            return False, "location outside the file"
        reasons = []
        for name, start, end, finalizer in self.methods:
            if start <= position < end and (finalizer or CLEANUP_METHOD.fullmatch(name)):
                reasons.append(f"in {'finalizer ~' if finalizer else 'method '}{name}")
        for start, end in self.finally_blocks:
            if start <= position < end:
                reasons.append(f"in a finally block (line {self.text.count(chr(10), 0, start) + 1})")
                break
        for name, start, end in self.types:
            word = lock_word(name)
            if word and start <= position < end:
                reasons.append(f"in type {name} ('{word}')")
        word = lock_word(PurePosixPath(path).stem)
        if word:
            reasons.append(f"in file {PurePosixPath(path).name} ('{word}')")
        return (True, "; ".join(dict.fromkeys(reasons))) if reasons else (False, "outside cleanup and lock code")


def _block(code, after):
    """Body span of a declaration whose header ends before the next '{' (or ';')."""
    brace, semicolon = code.find("{", after), code.find(";", after)
    if brace < 0 or 0 <= semicolon < brace:
        return after, after
    return brace, common.matching(code, brace, "{", "}")


def _methods(code):
    """Yield (name, body start, body end, is finalizer) for method-like declarations."""
    for match in _CALLABLE.finditer(code):
        name = match.group("name")
        if name in _NOT_DECLARATION or re.search(r"\bnew\s+(?:[\w.]+\s*\.\s*)?$", code[:match.start("name")]):
            continue
        close = common.matching(code, match.end() - 1, "(", ")")
        following = _AFTER_PARAMETERS.match(code, close)
        if following and following.group(0).rstrip().endswith("("):  # constructor initializer
            following = _AFTER_PARAMETERS.match(code, common.matching(code, following.end() - 1, "(", ")"))
        if not following or following.group(0).rstrip().endswith("("):
            continue
        finalizer = bool(match.group("tilde"))
        if following.group("brace"):
            start = following.end() - 1
            yield name, start, common.matching(code, start, "{", "}"), finalizer
        else:
            end = _expression_end(code, following.end())
            yield name, following.start("arrow"), end, finalizer


def _expression_end(code, index):
    depth = 0
    while index < len(code):
        char = code[index]
        depth += char in "([{"
        depth -= char in ")]}"
        if depth < 0 or (char == ";" and depth == 0):
            return index + 1
        index += 1
    return len(code)


def changed_lines(base, head, paths=None):
    """Map path -> set of head line numbers added or modified since base."""
    lines = {}
    for path, line, _ in common.added_lines(base, head, paths or ["."]):
        lines.setdefault(path, set()).add(line)
    return lines


def resolve_path(name, report, changed):
    """Map a report file key to a repository path among the changed files, or None.

    Stryker writes absolute paths (or paths relative to the report's projectRoot) of the
    machine it ran on; a key under this repository's root maps directly, any other key
    maps when it and exactly one changed path end alike (a checkout at another path, or a
    key relative to the project directory).
    """
    candidate = name.replace("\\", "/")
    root = (report.get("projectRoot") or "").replace("\\", "/").rstrip("/")
    if root and not candidate.startswith("/") and not re.match(r"[A-Za-z]:/", candidate):
        candidate = f"{root}/{candidate}"
    top = common.repo_root().replace("\\", "/").rstrip("/") + "/"
    if candidate.startswith(top):
        return candidate[len(top):] if candidate[len(top):] in changed else None
    matches = [path for path in changed if _suffix(candidate.split("/"), path.split("/"))]
    if len(matches) > 1:
        raise LookupError(f"Report file {name} matches several changed files: {', '.join(sorted(matches))}")
    return matches[0] if matches else None


def _suffix(left, right):
    """True when the shorter path is a component-wise suffix of the longer one."""
    size = min(len(left), len(right))
    return left[-size:] == right[-size:]


def evaluate(report_data, base, head, gate_report):
    """Return the list of mutant records on changed lines (survivors classified)."""
    changed = changed_lines(base, head)
    tree = common.Tree(head)
    records = []
    files = common.section(report_data, "files", dict, gate_report, "report")
    for name, entry in sorted(files.items()):
        try:
            path = resolve_path(name, report_data, changed)
        except LookupError as error:
            gate_report.error(str(error))
            continue
        if path is None or not isinstance(entry, dict):
            continue
        source = tree.read(path)
        if source is None:
            gate_report.error(f"{path} is in the report but not in {head}", path)
            continue
        embedded = entry.get("source")
        if isinstance(embedded, str) and embedded.replace("\r\n", "\n") != source.replace("\r\n", "\n"):
            gate_report.error(f"The report's copy of {path} differs from {head}; its line numbers cannot be "
                              "mapped (was Stryker run on another revision?)", path)
            continue
        structure = Structure(source)
        for mutant in entry.get("mutants", []):
            record = _record(path, mutant, changed[path], structure)
            if record:
                records.append(record)
    tree.close()
    return records


def _record(path, mutant, lines, structure):
    location = mutant.get("location") or {}
    start, end = location.get("start") or {}, location.get("end") or {}
    first = start.get("line")
    if not isinstance(first, int):
        return None
    last = end.get("line") if isinstance(end.get("line"), int) else first
    touched = sorted(line for line in lines if first <= line <= last)
    if not touched:
        return None
    record = {"path": path, "line": first, "endLine": last, "changedLines": touched,
              "id": str(mutant.get("id", "")), "mutator": mutant.get("mutatorName", "?"),
              "replacement": mutant.get("replacement", ""), "status": mutant.get("status", "?"),
              "statusReason": mutant.get("statusReason")}
    record["original"] = structure.snippet(first, start.get("column", 1), last, end.get("column"))
    scope, reason = structure.classify(path, first, start.get("column", 1))
    record["inBlockingScope"], record["reason"] = scope, reason
    record["blocking"] = scope and record["status"] in SURVIVING
    return record


def _cell(text, limit=80):
    text = " ".join(str(text or "").split())
    text = text if len(text) <= limit else text[:limit - 1] + "…"
    return text.replace("|", "\\|").replace("`", "'")


def render(records, base, head):
    survivors = [record for record in records if record["status"] in SURVIVING]
    counts = {}
    for record in records:
        counts[record["status"]] = counts.get(record["status"], 0) + 1
    lines = [f"Mutants whose span touches a line changed between `{base}` and `{head}`: "
             + (", ".join(f"{status} {count}" for status, count in sorted(counts.items())) or "none") + ".",
             "",
             "Each survivor below is a changed line whose behavior no test pins down: write a behavior test "
             "that fails when the code is changed as shown. This is a list of lines to test, not a score; "
             "do not chase a percentage, and do not edit the code merely to make a mutant disappear.",
             "",
             f"Blocking scope: {BLOCKING_SCOPE}. Timeout counts as detected. CompileError, RuntimeError and "
             "Pending mutants were not evaluated (warned about in cleanup and lock code); Ignored mutants are "
             "excluded by configuration."]
    if not survivors:
        lines += ["", "No surviving mutants on changed lines."]
        return "\n".join(lines)
    lines += ["", "| Location | Status | Mutator | Original | Replacement | Class | Reason |",
              "| --- | --- | --- | --- | --- | --- | --- |"]
    for record in sorted(survivors, key=lambda item: (not item["blocking"], item["path"], item["line"])):
        lines.append(f"| `{record['path']}:{record['line']}` | {record['status']} | {_cell(record['mutator'])} "
                     f"| `{_cell(record['original'])}` | `{_cell(record['replacement'])}` | {'**blocking**' if record['blocking'] else 'advisory'} "
                     f"| {_cell(record['reason'], 160)} |")
    return "\n".join(lines)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("report", help="Stryker JSON report (mutation-report.json)")
    parser.add_argument("--base", required=True, help="The merge-base the diff is taken from")
    parser.add_argument("--head", default="HEAD", help="The revision Stryker mutated")
    parser.add_argument("--json", dest="json_out", help="Also write the mutants on changed lines as JSON")
    parser.add_argument("--markdown", help="Also write the survivor list as markdown (for a PR body)")
    common.add_mode_arguments(parser)
    args = parser.parse_args(argv)
    advisory = common.net_advisory("mutation-gate", args.blocking)
    report = common.Report("Mutation survivors on changed lines")
    try:
        data = common.load_json_file(args.report)
    except (OSError, ValueError) as error:
        report.error(f"Cannot read the Stryker report {args.report}: {error}")
        return report.finish()  # a broken input fails in either mode
    records = evaluate(data, args.base, args.head, report)
    unusable = bool(report.errors)  # report/tree mismatch: the survivor list cannot be trusted
    for record in records:
        if record["blocking"]:
            report.error(f"Surviving mutant in cleanup/lock code ({record['reason']}): {record['mutator']} -> "
                         f"{_cell(record['replacement'])}; a line to write a behavior test for",
                         record["path"], record["line"])
        elif record["inBlockingScope"] and record["status"] in UNEVALUATED:
            report.warning(f"{record['status']} mutant in cleanup/lock code ({record['reason']}) was not evaluated: "
                           f"{record['mutator']} -> {_cell(record['replacement'])}; this changed line has no "
                           "mutation evidence either way", record["path"], record["line"])
    markdown = render(records, args.base, args.head)
    report.section(markdown)
    if args.json_out:
        with open(args.json_out, "w", encoding="utf-8") as handle:
            json.dump({"base": args.base, "head": args.head, "blockingScope": BLOCKING_SCOPE,
                       "mutants": records}, handle, indent=2)
            handle.write("\n")
    if args.markdown:
        with open(args.markdown, "w", encoding="utf-8") as handle:
            handle.write(markdown + "\n")
    return report.finish(advisory=advisory and not unusable)


if __name__ == "__main__":
    sys.exit(main())
