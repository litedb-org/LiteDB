"""Fault-point registry: every registered crash/fault hook must claim its evidence.

Enumerates the string-named fault hooks in LiteDB/ and compares them with
.github/safety/fault-points.json. A hook missing from the registry, a registry
entry whose hook is gone, a dynamic (non-literal) hook name, an unknown hook
family, or an evidence reference that does not resolve (or does not name the
hook) fails. A hook without evidence must state its gap explicitly.

Registered-hook coverage is not proof that the fault model is complete: in diff
mode the check also warns about newly added persistent I/O operations, so a
transition without any hook is discoverable, and the registry keeps a list of
known unhooked transitions.
"""
import argparse
import re
import sys

import safety_common as common

REGISTRY = f"{common.SAFETY_DIR}/fault-points.json"
SOURCE_ROOT = "LiteDB/"
# Named families: each call site passes the boundary's name as a string literal
# (or a choice between literals). (family, callee, argument index, path filter)
CALLEES = [
    ("process-crash", "CrashPoint", 0, None),
    ("process-crash", "TestCrashPoint", 0, None),
    ("process-crash", "SimulateProcessCrash?.Invoke", 0, None),
    ("rebuild-install", "SimulateInstallFailure?.Invoke", 0, None),
    ("checkpoint-stage", "CheckpointStage", 0, None),
    ("checkpoint-stage", "CheckpointStage?.Invoke", 0, None),
    ("coordination-file", "Observe", 1, re.compile(r"LiteDB/Client/Shared/SharedCoordinationFile\w*\.cs\Z")),
    ("coordination-file", "CreationStage?.Invoke", 1, None),
]
NAMED_DELEGATES = {"SimulateProcessCrash": "process-crash", "SimulateInstallFailure": "rebuild-install",
                   "CheckpointStage": "checkpoint-stage", "CreationStage": "coordination-file"}
CALL = re.compile(r"(?<![\w.])(?:[\w.]*\.)?(?P<callee>" + "|".join(
    sorted({re.escape(callee) for _, callee, _, _ in CALLEES}, key=len, reverse=True)) + r")\(")
DECLARATION = re.compile(r"\bvoid\s+(?:(?:Test)?CrashPoint|CheckpointStage|Observe)\s*\(\s*string\b")
# Every delegate field declared under #if DEBUG/TESTING is a test hook and must be
# classified: a named family above, a data-driven injector, or an observer.
DELEGATE = re.compile(r"\b(?:Action|Func)(?:<[^;=]*?>)?\s+([A-Z]\w*)\s*[;=]")
PERSISTENT_IO = re.compile(
    r"\bFlushToDisk\b|\.Flush\(\s*true\s*\)|\bSetLength\(|\bFile\.(?:Move|Replace|Delete|Copy)\("
    r"|\bDirectory\.(?:Move|Delete)\(|\bNativeFileSync\.\w+\(")
MODELS = {"exception", "process-death", "modeled-power-loss", "device", "corrupt-input", "interleaving", "capability"}


def source_hooks(tree, report):
    """Return ({(family, name): [(path, line)]}, {delegate: [(path, line)]}) found in the library."""
    hooks, delegates = {}, {}
    for path in tree.paths():
        if not (path.startswith(SOURCE_ROOT) and path.endswith(".cs")):
            continue
        text = common.blank_code(tree.read(path) or "", keep_strings=True)
        for match in CALL.finditer(text):
            candidates = [entry for entry in CALLEES if entry[1] == match.group("callee")
                          and (entry[3] is None or entry[3].match(path))]
            if not candidates:
                continue
            family, _, index, _ = candidates[0]
            line = text.count("\n", 0, match.start()) + 1
            arguments = _split_arguments(text[match.end():common.matching(text, match.end() - 1, "(", ")") - 1])
            argument = arguments[index] if index < len(arguments) else ""
            names = re.findall(r'"([^"]+)"', argument)
            rest = re.sub(r'"[^"]*"', "", argument)
            if names and re.fullmatch(r"[\w\s?:.!=&|()]*", rest):
                for name in names:  # a literal or a choice between literals
                    hooks.setdefault((family, name), []).append((path, line))
            elif not _forwards(text, match.start()):
                report.error("Fault hook without a literal name cannot be registered", path, line)
        for name, line in _testing_delegates(text):
            delegates.setdefault(name, []).append((path, line))
    return hooks, delegates


def _split_arguments(text):
    parts, depth, current = [], 0, ""
    for char in text:
        depth += char in "([{<"
        depth -= char in ")]}>"
        if char == "," and depth == 0:
            parts.append(current)
            current = ""
        else:
            current += char
    return parts + [current]


def _testing_delegates(text):
    """Yield (name, line) for delegate fields declared inside #if DEBUG/TESTING regions."""
    regions = []
    for number, line in enumerate(text.splitlines(), 1):
        stripped = line.strip()
        if stripped.startswith("#if"):
            regions.append("TESTING" in stripped or "DEBUG" in stripped)
        elif stripped.startswith("#elif") and regions:
            regions[-1] = "TESTING" in stripped or "DEBUG" in stripped
        elif stripped.startswith("#else") and regions:
            regions[-1] = False
        elif stripped.startswith("#endif") and regions:
            regions.pop()
        elif any(regions) and "(" not in line.split("=")[0]:
            match = DELEGATE.search(line)
            if match and not re.search(r"\b(?:var|return|new)\b", line[:match.start(1)]):
                yield match.group(1), number


def _forwards(text, position):
    """True for the declaration of a hook method or the call forwarding its parameter
    (within a few lines of the declaration, which may also count its reachability marker)."""
    start = position
    for _ in range(6):
        start = text.rfind("\n", 0, max(start - 1, 0)) + 1
        if start <= 0:
            break
    return bool(DECLARATION.search(text[start:text.find("\n", position)]))


def check_registry(tree, report):
    registry = tree.read_json(REGISTRY)
    if registry is None:
        report.error(f"{REGISTRY} is missing")
        return {}, []
    found, delegates = source_hooks(tree, report)
    hooks, injectors_list, observers, unhooked = (
        common.section(registry, key, kind, report, REGISTRY)
        for key, kind in (("hooks", list), ("injectors", list), ("observers", dict), ("unhookedTransitions", list)))
    entries = _index(hooks, lambda entry: (entry.get("family"), entry.get("name")), report)
    injector_entries = _index(injectors_list, lambda entry: ("injector", entry.get("name")), report)
    _compare(found, entries, "Fault hook", report)
    injectors = {("injector", name): locations for name, locations in delegates.items()
                 if name not in NAMED_DELEGATES and name not in observers}
    _compare(injectors, injector_entries, "Test hook", report)
    for name, reason in sorted(observers.items()):
        if name not in delegates:
            report.error(f"Observer hook {name} no longer exists in {SOURCE_ROOT}", REGISTRY)
        if len((reason or "").strip()) < 10:
            report.error(f"Observer hook {name}: explain why it is not a fault boundary", REGISTRY)
    targets = common.fuzz_targets(tree)
    scheduled = common.scheduled_fuzz_targets(tree)
    for key, entry in sorted({**entries, **injector_entries}.items()):
        check_entry(tree, key, entry, targets, scheduled, report)
    for item in unhooked:
        if not item.get("protocol") or not item.get("transition"):
            report.error("Each unhooked transition needs a protocol and a transition", REGISTRY)
    return {**entries, **injector_entries}, unhooked


def _index(items, key, report):
    entries = {}
    for entry in items:
        if key(entry) in entries:
            report.error(f"{key(entry)[0]}:{key(entry)[1]} is registered twice", REGISTRY)
        entries[key(entry)] = entry
    return entries


def _compare(found, entries, noun, report):
    def label(key):
        return key[1] if key[0] == "injector" else f"{key[0]}:{key[1]}"

    for key, locations in sorted(found.items()):
        if key not in entries:
            path, line = locations[0]
            hint = " (as an injector with evidence, or under observers with a reason)" if key[0] == "injector" else ""
            report.error(f"{noun} {label(key)} is not registered in {REGISTRY}{hint}", path, line)
    for key in sorted(set(entries) - set(found)):
        report.error(f"Registered {noun.lower()} {label(key)} no longer exists in {SOURCE_ROOT}", REGISTRY)


def check_entry(tree, key, entry, targets, scheduled, report):
    label = f"{key[0]}:{key[1]}"
    if not entry.get("protocol"):
        report.error(f"{label}: name the protocol this boundary belongs to", REGISTRY)
    evidence = common.section(entry, "evidence", list, report, REGISTRY)
    if not evidence and len((entry.get("gap") or "").strip()) < 10:
        report.error(f"{label}: no evidence claims this hook; add evidence or state the gap", REGISTRY)
    for item in evidence:
        if item.get("model") not in MODELS:
            report.error(f"{label}: evidence model must be one of {', '.join(sorted(MODELS))}", REGISTRY)
        sources = [tree.read(via) or "" for via in item.get("via", []) if isinstance(via, str)]
        if item.get("test"):
            resolved = common.resolve_test(tree, item["test"])
            if resolved is None:
                report.error(f"{label}: test {item['test']} does not resolve", REGISTRY)
                continue
            sources.append(_own_text(tree.read(resolved[0]), resolved[1]))
        elif item.get("fuzz"):
            if item["fuzz"] not in targets:
                report.error(f"{label}: fuzz target {item['fuzz']} does not exist", REGISTRY)
                continue
            if item["fuzz"] not in scheduled:
                report.error(f"{label}: fuzz target {item['fuzz']} is not run by {common.FUZZ_WORKFLOW}", REGISTRY)
            sources.append(tree.read(targets[item["fuzz"]]) or "")
        else:
            report.error(f"{label}: evidence needs a test or fuzz reference", REGISTRY)
            continue
        token = key[1] if key[0] == "injector" else f'"{key[1]}"'
        if not any(token in source for source in sources):
            claim = item.get("test") or f"fuzz:{item.get('fuzz')}"
            report.error(f"{label}: {claim} never names the hook (nor does its 'via'); the claim is unverifiable",
                         REGISTRY)


def _own_text(text, method):
    """The test file without the bodies of its other test methods: the referenced
    method plus shared members (MemberData, constants, helpers) may name the hook."""
    for other in common.parse_tests(text).values():
        if other.fqn != method.fqn:
            text = text[:other.start] + " " * (other.end - other.start) + text[other.end:]
    return text


def warn_new_io(base, head, report):
    """Flag persistent I/O added without a nearby registered hook."""
    for path, line, text in common.added_lines(base, head, [SOURCE_ROOT]):
        if path.endswith(".cs") and PERSISTENT_IO.search(common.blank_code(text)):
            report.warning("New persistent I/O operation: register a fault point for this boundary or record "
                           f"it under unhookedTransitions in {REGISTRY}", path, line)


def summarize(entries, unhooked, report):
    rows = ["| Hook | Protocol | Evidence | Gap |", "| --- | --- | --- | --- |"]
    for (family, name), entry in sorted(entries.items(), key=lambda item: (str(item[1].get("protocol", "")), item[0])):
        claims = ", ".join(item.get("test", "").split("#")[-1] or f"fuzz:{item.get('fuzz')}"
                           for item in entry.get("evidence", []) if isinstance(item, dict))
        rows.append(f"| `{family}:{name}` | {entry.get('protocol')} | {claims or '**none**'} | {entry.get('gap', '')} |")
    report.section("\n".join(rows))
    if unhooked:
        report.section("Known persistent transitions without a hook:\n\n" + "\n".join(
            f"- {item.get('protocol')}: {item.get('transition')}"
            + (f" (#{item['issue']})" if item.get("issue") else "") for item in unhooked))


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--base", help="Also warn about persistent I/O added since this revision")
    parser.add_argument("--head", default="HEAD")
    args = parser.parse_args(argv)
    head = common.Tree(args.head)
    report = common.Report("Fault-point registry")
    try:
        entries, unhooked = check_registry(head, report)
    except common.MalformedJson as error:
        report.error(str(error), REGISTRY)
        entries, unhooked = {}, []
    if args.base:
        warn_new_io(args.base, args.head, report)
    summarize(entries, unhooked, report)
    return report.finish()


if __name__ == "__main__":
    sys.exit(main())
