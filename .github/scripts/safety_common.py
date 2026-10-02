"""Shared helpers for the safety-evidence checks (see docs/rules/safety-evidence.md).

The checks read git revisions rather than the working tree, so they can judge any
candidate tree (a PR merge ref, a scratch merge, a merge-queue commit) without a
checkout. C# is scanned with a small lexer that blanks comments (and optionally
string literals) while preserving offsets, so structure found in the blanked text
can be read back from the original.
"""
import json
import os
import re
import subprocess
import weakref
from dataclasses import dataclass, field
from pathlib import Path

SAFETY_DIR = ".github/safety"
FUZZ_TARGETS_DIR = "LiteDB.Fuzz/Targets/"
FUZZ_WORKFLOW = ".github/workflows/fuzz.yml"
WORKFLOWS_DIR = ".github/workflows/"


def git(*args, cwd=None, binary=False):
    output = subprocess.check_output(["git", *args], cwd=cwd or repo_root())
    return output if binary else output.decode("utf-8-sig", errors="replace")


_ROOT = []


def repo_root():
    """The repository top level, so paths are repository-relative from any directory."""
    if not _ROOT:
        top = subprocess.check_output(["git", "rev-parse", "--show-toplevel"]).decode("utf-8").strip()
        _ROOT.append(top)
    return _ROOT[0]


WORKTREE = "WORKTREE"


class MalformedJson(ValueError):
    """A registry or ledger file that does not parse; reported instead of a traceback."""


def section(data, key, kind, report, path):
    """data[key] when it has the expected JSON type (list items must be objects); else report it."""
    value = data.get(key, kind()) if isinstance(data, dict) else None
    if not isinstance(value, kind):
        report.error(f"{path}: '{key}' must be a JSON {'array' if kind is list else 'object'}", path)
        return kind()
    if kind is list and not all(isinstance(item, dict) for item in value):
        report.error(f"{path}: every entry of '{key}' must be a JSON object", path)
        return [item for item in value if isinstance(item, dict)]
    return value


class Tree:
    """Read-only view of the files of one git revision (or WORKTREE for local files)."""

    def __init__(self, rev, cwd=None):
        self.rev = rev
        self.cwd = cwd or repo_root()
        self._paths = None
        self._cache = {}
        self._state = {"batch": None}
        weakref.finalize(self, Tree._shutdown, self._state)

    def paths(self):
        if self._paths is None:
            if self.rev == WORKTREE:
                listing = git("ls-files", "-z", "--cached", "--others", "--exclude-standard", cwd=self.cwd)
                root = Path(self.cwd)
                names = [name for name in listing.split("\0") if name and (root / name).is_file()]
            else:
                names = git("ls-tree", "-r", "-z", "--name-only", self.rev, cwd=self.cwd).split("\0")
            self._paths = sorted(filter(None, names))
        return self._paths

    def exists(self, path):
        if getattr(self, "_path_set", None) is None:
            self._path_set = set(self.paths())
        return path in self._path_set

    def read(self, path):
        """Return the decoded file content, or None when the revision lacks it."""
        if path not in self._cache:
            self._cache[path] = self._read_blob(path)
        return self._cache[path]

    def read_json(self, path, default=None):
        text = self.read(path)
        if text is None:
            return default
        try:
            return json.loads(text)
        except ValueError as error:
            raise MalformedJson(f"{path} is not valid JSON at {self.rev}: {error}") from error

    def _read_blob(self, path):
        if self.rev == WORKTREE:
            file = Path(self.cwd) / path
            return file.read_text(encoding="utf-8-sig", errors="replace") if file.is_file() else None
        batch = self._state["batch"]
        if batch is None:
            batch = self._state["batch"] = subprocess.Popen(
                ["git", "cat-file", "--batch"], cwd=self.cwd,
                stdin=subprocess.PIPE, stdout=subprocess.PIPE)
        batch.stdin.write(f"{self.rev}:{path}\n".encode("utf-8"))
        batch.stdin.flush()
        header = batch.stdout.readline().decode("utf-8").split()
        if len(header) < 3:  # "<name> missing"
            return None
        content = batch.stdout.read(int(header[2]))  # consume every object type, or the stream desyncs
        batch.stdout.read(1)
        if header[1] != "blob":  # a directory (tree) or submodule (commit)
            return None
        return content.decode("utf-8-sig", errors="replace")

    def close(self):
        Tree._shutdown(self._state)

    @staticmethod
    def _shutdown(state):
        """Stop the batch reader; also runs when the Tree is garbage-collected."""
        batch, state["batch"] = state["batch"], None
        if batch is not None:
            batch.stdin.close()
            batch.stdout.close()
            batch.wait()


def _revisions(base, head):
    return [base] if head == WORKTREE else [base, head]


def changed_files(base, head, cwd=None):
    """Map changed path -> status letter (A, M, D; renames become D + A)."""
    output = git("diff", "--name-status", "--no-renames", "-z", *_revisions(base, head), cwd=cwd)
    fields = [value for value in output.split("\0") if value]
    changes = {fields[index + 1]: fields[index][0] for index in range(0, len(fields) - 1, 2)}
    if head == WORKTREE:
        untracked = git("ls-files", "-z", "--others", "--exclude-standard", cwd=cwd).split("\0")
        changes.update({path: "A" for path in untracked if path})
    return changes


def added_lines(base, head, paths, cwd=None):
    """Yield (path, head line number, text) for lines added between two revisions."""
    diff = git("diff", "-U0", "--no-renames", *_revisions(base, head), "--", *paths, cwd=cwd)
    path = line = None
    for raw in diff.splitlines():
        if raw.startswith("+++ "):
            path = None if raw[4:] == "/dev/null" else raw[6:]
        elif raw.startswith("@@"):
            line = int(re.match(r"@@ -\S+ \+(\d+)", raw).group(1))
        elif raw.startswith("+") and path is not None:
            yield path, line, raw[1:]
            line += 1


def diff_lines(base, head, paths, cwd=None):
    """Yield (sign, path, line, text): '-' lines numbered in base, '+' lines numbered in head."""
    diff = git("diff", "-U0", "--no-renames", *_revisions(base, head), "--", *paths, cwd=cwd)
    old_path = new_path = None
    old = new = 0
    for raw in diff.splitlines():
        if raw.startswith("--- "):
            old_path = None if raw[4:] == "/dev/null" else raw[6:]
        elif raw.startswith("+++ "):
            new_path = None if raw[4:] == "/dev/null" else raw[6:]
        elif raw.startswith("@@"):
            match = re.match(r"@@ -(\d+)(?:,\d+)? \+(\d+)", raw)
            old, new = int(match.group(1)), int(match.group(2))
        elif raw.startswith("-") and old_path is not None:
            yield "-", old_path, old, raw[1:]
            old += 1
        elif raw.startswith("+") and new_path is not None:
            yield "+", new_path, new, raw[1:]
            new += 1


def merge_base(head="HEAD", upstream="origin/dev", cwd=None):
    return git("merge-base", head, upstream, cwd=cwd).strip()


# --- C# scanning -----------------------------------------------------------

def blank_code(text, keep_strings=False):
    """Replace comments (and string/char literal contents) by spaces, keeping offsets."""
    out = list(text)
    index, length = 0, len(text)

    def blank(start, end):
        for position in range(start, end):
            if out[position] != "\n":
                out[position] = " "

    while index < length:
        char = text[index]
        if text.startswith("//", index):
            end = text.find("\n", index)
            end = length if end < 0 else end
            blank(index, end)
            index = end
        elif text.startswith("/*", index):
            end = text.find("*/", index + 2)
            end = length if end < 0 else end + 2
            blank(index, end)
            index = end
        elif char == '"' or (char in "@$" and re.match(r'[@$]{1,2}"', text[index:index + 3])):
            end = _string_end(text, index)
            if not keep_strings:
                blank(index, end)
            index = end
        elif char == "'":
            match = re.match(r"'(?:\\.[^']{0,8}|[^'\\\n])'", text[index:index + 12])
            end = index + (len(match.group(0)) if match else 1)
            if not keep_strings:
                blank(index, end)
            index = end
        else:
            index += 1
    return "".join(out)


def _string_end(text, start):
    prefix = re.match(r'[@$]*', text[start:]).group(0)
    quote = start + len(prefix)
    quotes = len(re.match(r'"*', text[quote:]).group(0))
    if quotes >= 3:  # raw string literal
        end = text.find('"' * quotes, quote + quotes)
        return len(text) if end < 0 else end + quotes
    index = quote + 1
    verbatim = "@" in prefix
    interpolated = "$" in prefix
    while index < len(text):
        char = text[index]
        if interpolated and char == "{":
            if text.startswith("{{", index):
                index += 2
                continue
            index = _hole_end(text, index + 1)
            continue
        if verbatim and char == '"':
            if text.startswith('""', index):
                index += 2
                continue
            return index + 1
        if not verbatim and char == "\\":
            index += 2
            continue
        if char == '"' or (char == "\n" and not verbatim):
            return index + 1
        index += 1
    return len(text)


def _hole_end(text, index):
    """Index just past the '}' closing an interpolation hole; nested literals are code-aware."""
    depth = 1
    while index < len(text):
        char = text[index]
        if char == '"' or (char in "@$" and re.match(r'[@$]{1,2}"', text[index:index + 3])):
            index = _string_end(text, index)
            continue
        if char == "'":
            match = re.match(r"'(?:\\.[^']{0,8}|[^'\\\n])'", text[index:index + 12])
            index += len(match.group(0)) if match else 1
            continue
        depth += char == "{"
        depth -= char == "}"
        index += 1
        if depth == 0:
            return index
    return len(text)


def matching(text, open_index, opener, closer):
    """Index just past the bracket closing the one at open_index (blanked text)."""
    depth = 0
    for index in range(open_index, len(text)):
        if text[index] == opener:
            depth += 1
        elif text[index] == closer:
            depth -= 1
            if depth == 0:
                return index + 1
    return len(text)


@dataclass
class TestMethod:
    fqn: str
    name: str
    line: int
    start: int
    end: int
    kinds: list = field(default_factory=list)
    skips: list = field(default_factory=list)

    @property
    def skipped_variants(self):
        return sum(1 for skip in self.skips if skip is not None)


_STRUCTURE = re.compile(
    r"(?P<ns>\bnamespace\s+(?P<nsname>[\w.]+)\s*(?P<nsend>[;{]))"
    r"|(?P<type>\b(?:class|struct|record|interface)\s+(?P<tname>[A-Za-z_]\w*))"
    r"|(?P<open>\{)|(?P<close>\})|(?P<semi>;)"
    r"|(?P<attr>\[\s*(?:\w+\.)*(?P<kind>\w*(?:Fact|Theory))(?:Attribute)?\s*(?=[(\],]))")
_SKIP = re.compile(r'\bSkip\s*=\s*(?:@?"((?:[^"\\]|\\.)*)"|([^,)\]]+))')
_BETWEEN = re.compile(r"\s*(?:#[^\n]*\n\s*|\[)")


def parse_tests(text):
    """Return {fqn: TestMethod} for xUnit test methods declared in C# source."""
    code = blank_code(text)
    stack, file_namespace, pending, attributes = [], "", None, []
    for match in _STRUCTURE.finditer(code):
        if match.group("ns"):
            if match.group("nsend") == ";":
                file_namespace = match.group("nsname")
            else:
                stack.append(("ns", match.group("nsname")))
        elif match.group("type"):
            pending = match.group("tname")
        elif match.group("open"):
            stack.append(("type", pending) if pending else ("block", None))
            pending = None
        elif match.group("close"):
            if stack:
                stack.pop()
        elif match.group("semi"):
            pending = None
        elif match.group("attr"):
            namespaces = [file_namespace] + [name for kind, name in stack if kind == "ns"]
            types = [name for kind, name in stack if kind == "type"]
            scope = ".".join(filter(None, namespaces))
            owner = "+".join(types)
            attributes.append((match.start(), match.group("kind"), f"{scope}.{owner}".strip(".")))
    return _attach_methods(text, code, attributes)


def _attach_methods(text, code, attributes):
    tests, consumed = {}, set()
    for position, kind, owner in attributes:
        if position in consumed:
            continue
        variants = [(position, kind)]
        cursor = matching(code, position, "[", "]")
        while True:  # absorb #if/#else variants and further attribute sections
            gap = _BETWEEN.match(code, cursor)
            if not gap:
                break
            if gap.group(0).rstrip().endswith("["):
                section = gap.end() - 1
                nested = next((item for item in attributes if item[0] == section), None)
                if nested:
                    consumed.add(section)
                    variants.append((section, nested[1]))
                cursor = matching(code, section, "[", "]")
            else:
                cursor = gap.end()
        paren = code.find("(", cursor)
        declaration = re.sub(r"<[^<>]*>\s*$", "", code[cursor:paren].rstrip())
        name_match = re.search(r"([A-Za-z_]\w*)\s*$", declaration)
        if paren < 0 or not name_match:
            continue
        name = name_match.group(1)
        end = _body_end(code, matching(code, paren, "(", ")"))
        skips = [_skip_reason(text, start, matching(code, start, "[", "]")) for start, _ in variants]
        fqn = f"{owner}.{name}"
        method = tests.get(fqn)
        if method is None:
            line = code.count("\n", 0, cursor + name_match.start(1)) + 1
            method = tests[fqn] = TestMethod(fqn, name, line, position, end)
        method.kinds.extend(kind for _, kind in variants)
        method.skips.extend(skips)
        method.end = max(method.end, end)
    return tests


def _body_end(code, after_parameters):
    brace = code.find("{", after_parameters)
    arrow = code.find("=>", after_parameters)
    if arrow >= 0 and (brace < 0 or arrow < brace):
        semicolon = code.find(";", arrow)
        return len(code) if semicolon < 0 else semicolon + 1
    return len(code) if brace < 0 else matching(code, brace, "{", "}")


def _skip_reason(text, start, end):
    match = _SKIP.search(text, start, end)
    if not match:
        return None
    return match.group(1) if match.group(1) is not None else match.group(2).strip()


def is_test_path(path):
    first = path.split("/", 1)[0]
    return path.endswith(".cs") and first.endswith(".Tests")


def collect_tests(tree):
    """Return {fqn: (path, TestMethod)} for every test project in the tree."""
    cached = getattr(tree, "_tests", None)
    if cached is not None:
        return cached
    tests = {}
    for path in tree.paths():
        if is_test_path(path):
            for fqn, method in parse_tests(tree.read(path) or "").items():
                tests.setdefault(fqn, (path, method))
    tree._tests = tests
    return tests


def resolve_test(tree, reference):
    """Resolve 'path#Method' to (path, TestMethod) or None."""
    path, separator, name = reference.partition("#")
    text = tree.read(path) if separator and is_test_path(path) else None
    if text is None:
        return None
    return next(((path, method) for method in parse_tests(text).values() if method.name == name), None)


def fuzz_targets(tree):
    """Return {target name: source path} for LiteDB.Fuzz targets."""
    targets = {}
    for path in tree.paths():
        if path.startswith(FUZZ_TARGETS_DIR) and path.endswith(".cs"):
            for name in re.findall(r'\bstring\s+Name\s*=>\s*"([^"]+)"', tree.read(path) or ""):
                targets[name] = path
    return targets


def scheduled_fuzz_targets(tree):
    """Target names that fuzz.yml runs in any job (smoke, checksums, nightly, ...)."""
    workflow = tree.read(FUZZ_WORKFLOW) or ""
    names = set()
    for value in re.findall(r"(?:targets:|--target)\s+\"?([a-z0-9,\-]+)", workflow):
        names.update(filter(None, value.split(",")))
    return names


def workflow_text(tree):
    return "\n".join(tree.read(path) or "" for path in tree.paths()
                     if path.startswith(WORKFLOWS_DIR) and path.endswith((".yml", ".yaml")))


def markdown_anchors(text):
    anchors, seen = set(), {}
    for heading in re.findall(r"^#{1,6}\s+(.+?)\s*#*\s*$", text, re.M):
        slug = re.sub(r"[^\w\- ]", "", heading.strip().lower()).replace(" ", "-")
        count = seen.get(slug, 0)
        seen[slug] = count + 1
        anchors.add(slug if count == 0 else f"{slug}-{count}")
    return anchors


def comment_lines(text):
    """Map line number -> the comment text on that line (C# //, ///, /* */), markers stripped."""
    code = blank_code(text, keep_strings=True)
    comments = {}
    for number, (original, blanked) in enumerate(zip(text.split("\n"), code.split("\n")), 1):
        comment = "".join(char if char != other else " " for char, other in zip(original, blanked))
        comment = re.sub(r"^[\s/*]+|[\s*/]+$", "", re.sub(r"\s+", " ", comment))
        if comment:
            comments[number] = comment
    return comments


# --- documentation sentences ------------------------------------------------

ANCHOR = re.compile(r"\[(test|marker):\s*([^\]\s]+)\s*\]")
_SENTENCE_END = re.compile(r"(?<=[.!?])\s+(?=[A-Z`*_(\"]|\[(?!test:|marker:))")


def doc_sentences(text):
    """Yield (first line, last line, sentence) for markdown prose; code fences and headings skipped.

    Paragraphs, list items and table rows are units; a unit is split after '.', '!' or '?'
    unless an anchor such as '[test: ...]' follows, so a trailing anchor stays on its sentence.
    """
    unit, start, fenced = [], 0, False
    for number, line in enumerate(text.split("\n") + [""], 1):
        stripped = line.strip()
        if stripped.startswith(("```", "~~~")):
            fenced = not fenced
        heading = re.match(r"#{1,6}\s", stripped)
        boundary = fenced or not stripped or heading or stripped.startswith(("```", "~~~", "|", "- ", "* ", "> ")) \
            or re.match(r"\d+\.\s", stripped)
        if boundary and unit:
            yield from _split_unit(unit, start)
            unit = []
        if fenced or not stripped or heading or stripped.startswith(("```", "~~~")):
            continue
        if stripped.startswith("|"):
            yield number, number, stripped
            continue
        if not unit:
            start = number
        unit.append(stripped)


def _split_unit(lines, start):
    text = " ".join(lines)
    offsets = [0]
    for line in lines[:-1]:
        offsets.append(offsets[-1] + len(line) + 1)
    position = 0
    for piece in _SENTENCE_END.split(text):
        index = text.index(piece, position)
        first = start + sum(1 for offset in offsets if offset <= index) - 1
        last = start + sum(1 for offset in offsets if offset < index + len(piece)) - 1
        position = index + len(piece)
        yield first, last, piece


def normalize_prose(text):
    return re.sub(r"\s+", " ", text).strip().lower()


def declared_markers(tree):
    """Marker names from .github/safety/markers.json, or None when the registry does not exist."""
    data = tree.read_json(f"{SAFETY_DIR}/markers.json")
    if data is None:
        return None
    items = data.get("markers", data) if isinstance(data, dict) else data
    names = set(items) if isinstance(items, dict) else {
        item if isinstance(item, str) else item.get("name") for item in items if isinstance(item, (str, dict))}
    # Fault-point markers derive from the fault-point registry instead of being listed twice.
    faults = tree.read_json(f"{SAFETY_DIR}/fault-points.json", {}) or {}
    names |= {f"fault-point:{entry.get('name')}" for key in ("hooks", "injectors")
              for entry in faults.get(key, []) if isinstance(entry, dict)}
    return names - {None}


def resolve_test_anchor(tree, reference):
    """'path#Method', 'Class#Method' or 'Namespace.Class.Method' -> (path, TestMethod) or None.

    A path may also name a .github/scripts/test_*.py unittest method (returns (path, name)).
    """
    if "#" in reference and "/" in reference.split("#")[0]:
        path, _, name = reference.partition("#")
        if re.match(r"\.github/scripts/test_\w+\.py\Z", path):  # a CI-script unittest
            text = tree.read(path) or ""
            return (path, name) if re.search(r"^\s+def " + re.escape(name) + r"\(", text, re.M) else None
        return resolve_test(tree, reference)
    owner, _, name = reference.rpartition("#") if "#" in reference else reference.rpartition(".")
    for fqn, found in collect_tests(tree).items():
        if found[1].name == name and re.search(r"(?:^|[.+])" + re.escape(owner) + r"\Z", fqn[:-len(name) - 1]):
            return found
    return None


_CODE_REFERENCE = re.compile(r"(?<![\w/.-])((?:[\w.-]+/)+[\w.-]+\.\w+)(?:#(\w+)|:(\d+))?")


def resolve_references(tree, text):
    """Yield (reference, verdict) for anchors and repository paths named in text.

    verdict is True (resolves), False (does not) or None (a marker while no marker
    registry exists yet). Anchors are '[test: path-or-Class#Method]' and '[marker: name]';
    paths may carry '#Method' (a test) or ':line'.
    """
    markers = None
    for kind, value in ANCHOR.findall(text):
        if kind == "marker":
            markers = declared_markers(tree) if markers is None else markers
            yield f"[marker: {value}]", None if markers is None else value in markers
        else:
            yield f"[test: {value}]", resolve_test_anchor(tree, value) is not None
    for match in _CODE_REFERENCE.finditer(ANCHOR.sub("", text)):
        path, method, line = match.groups()
        content = tree.read(path) if tree.exists(path) else None
        if method:
            verdict = resolve_test(tree, f"{path}#{method}") is not None
        else:
            verdict = content is not None and (not line or int(line) <= content.count("\n") + 1)
        yield match.group(0), verdict


def glob_regex(pattern):
    """Translate a path glob with ** support into a compiled regex."""
    parts, index = [], 0
    while index < len(pattern):
        if pattern.startswith("**/", index):
            parts.append("(?:.*/)?")
            index += 3
        elif pattern.startswith("**", index):
            parts.append(".*")
            index += 2
        elif pattern[index] == "*":
            parts.append("[^/]*")
            index += 1
        elif pattern[index] == "?":
            parts.append("[^/]")
            index += 1
        else:
            parts.append(re.escape(pattern[index]))
            index += 1
    return re.compile("".join(parts) + r"\Z")


# --- reporting -------------------------------------------------------------

class Report:
    """Collects errors/warnings, prints GitHub annotations and a step summary."""

    def __init__(self, title):
        self.title = title
        self.errors, self.warnings, self.sections = [], [], []

    def error(self, message, path=None, line=None):
        self.errors.append(message)
        _annotate("error", message, path, line)

    def warning(self, message, path=None, line=None):
        self.warnings.append(message)
        _annotate("warning", message, path, line)

    def section(self, markdown):
        self.sections.append(markdown)

    def finish(self, advisory=False):
        """Print the summary; exit code 1 on errors unless the net runs in advisory mode."""
        status = "failed" if self.errors else "passed"
        if self.errors and advisory:
            status = f"{len(self.errors)} finding(s), advisory (exit 0; see {NET_MODES})"
        lines = [f"## {self.title}: {status}", ""]
        lines += [f"- :x: {message}" for message in self.errors]
        lines += [f"- :warning: {message}" for message in self.warnings]
        lines += [""] + self.sections
        summary = "\n".join(lines) + "\n"
        target = os.environ.get("GITHUB_STEP_SUMMARY")
        if target:
            with open(target, "a", encoding="utf-8") as handle:
                handle.write(summary)
        else:
            print(summary)
        return 1 if self.errors and not advisory else 0


def _annotate(level, message, path, line):
    if os.environ.get("GITHUB_ACTIONS") != "true":
        location = f"{path}:{line}: " if path and line else (f"{path}: " if path else "")
        print(f"{level.upper()}: {location}{message}")
        return
    properties = ",".join(filter(None, [f"file={path}" if path else "", f"line={line}" if line else ""]))
    escaped = message.replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A")
    print(f"::{level}{' ' + properties if properties else ''}::{escaped}")


# --- advisory/blocking switch for the diff nets ------------------------------

NET_MODES = f"{SAFETY_DIR}/net-modes.json"


def add_mode_arguments(parser):
    """--advisory/--blocking override the single switch in net-modes.json for a local run."""
    group = parser.add_mutually_exclusive_group()
    group.add_argument("--advisory", dest="blocking", action="store_false", default=None,
                       help=f"Report findings but exit 0 (default: per {NET_MODES})")
    group.add_argument("--blocking", dest="blocking", action="store_true", help="Exit 1 on findings")


def net_advisory(net, blocking=None):
    """True when `net` reports without failing. One switch: net-modes.json "blocking" for every net it
    lists; a net it does not list, or a missing file, is blocking. An explicit argument wins."""
    if blocking is not None:
        return not blocking
    path = Path(repo_root()) / NET_MODES
    if not path.is_file():
        return False
    data = load_json_file(path)
    return net in data.get("nets", {}) and not data.get("blocking", True)


def load_json_file(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))
