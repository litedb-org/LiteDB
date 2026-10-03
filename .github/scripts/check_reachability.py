"""Reachability markers: every situation a campaign must reach is registered and hit.

A marker is a TESTING-only `Reachability.Sometimes("<family>:<name>")` call (or
`Reachability.FaultPoint(...)` for a registered fault hook) that counts how often a
situation occurred. LiteDB.Fuzz writes the counts of each run to `markers.json`.

Static mode (no options) compares the literal markers in LiteDB/ and LiteDB.Fuzz/
with .github/safety/markers.json: an unregistered or non-literal marker, a registered
marker that no longer exists, a fault hook site without a marker, or a registry entry
whose paths, targets or doc do not resolve fails. Fault-point markers are derived from
.github/safety/fault-points.json and are not listed in markers.json; every fault point is
expected to be reached by the PR smoke campaign unless `faultPointGates` says otherwise.

`--base <rev>` adds the markers declared for the diff: new or changed registry
entries, markers whose `paths` intersect the changed files, fault points added by the
diff, and public members added or changed on the public API interfaces or in a new
public type, each of which needs a registered `api:<Type>.<Member>` marker.

`--runs <dir> [...]` aggregates every markers.json below the directories, prints all
markers with their hit counts and the never-hit ones, writes `--output` (JSON), and
fails when a marker declared for the diff (with `--base`) has no hit. A declared
marker whose registry entry is `"gate": "advisory"` is reported but does not fail.
"""
import argparse
import json
import re
import sys
from pathlib import Path

import check_fault_points as fault_points
import safety_common as common

REGISTRY = f"{common.SAFETY_DIR}/markers.json"
SCANNED = ("LiteDB/", "LiteDB.Fuzz/")
KINDS = {"fault-point", "maintenance", "refusal", "api", "situation"}
GATES = {"smoke", "advisory"}
MARKER = re.compile(r"(?<![\w.])(?:(?:LiteDB\.)?Utils\.)?Reachability\.(?P<method>Sometimes|FaultPoint)\(")
NAME = re.compile(r"(?P<kind>[a-z-]+):[A-Za-z0-9][\w.\-]*\Z")
# Fault hooks whose call goes through one method that counts the marker for every caller.
DISPATCHERS = {"CrashPoint": "LiteDB/Engine/EngineState.cs", "TestCrashPoint": "LiteDB/Engine/EngineState.cs",
               "CheckpointStage": "LiteDB/Engine/Disk/DiskService.Checkpoint.cs",
               "Observe": "LiteDB/Client/Shared/SharedCoordinationFile.cs",
               "TeardownSteps.Before": "LiteDB/Utils/TeardownSteps.cs", "TeardownSteps.After": "LiteDB/Utils/TeardownSteps.cs",
               "Step": "LiteDB/Utils/TryCatch.cs"}
API_TYPES = ("ILiteDatabase", "ILiteCollection", "ILiteQueryable", "ILiteQueryableResult", "ILiteEngine",
             "ILiteStorage", "ILiteRepository")
TYPE = re.compile(r"\bpublic\s+(?:(?:static|sealed|abstract|partial|readonly)\s+)*"
                  r"(?P<kind>class|interface|struct|record|enum)\s+(?P<name>[A-Za-z_]\w*)")
MEMBER = re.compile(r"(?P<name>[A-Za-z_]\w*|this)\s*(?:<[^;{()]*?>)?\s*[\[({]|(?P<field>[A-Za-z_]\w*)\s*[;=]")


def code_markers(tree, report):
    """Return ({name: [(path, line)]}, {path: [(method, argument, line)]}) for marker calls."""
    literal, calls = {}, {}
    for path in tree.paths():
        if not (path.startswith(SCANNED) and path.endswith(".cs")):
            continue
        text = common.blank_code(tree.read(path) or "", keep_strings=True)
        for match in MARKER.finditer(text):
            line = text.count("\n", 0, match.start()) + 1
            argument = text[match.end():common.matching(text, match.end() - 1, "(", ")") - 1].strip()
            calls.setdefault(path, []).append((match.group("method"), argument, line))
            name = argument[1:-1] if re.fullmatch(r'"[^"]*"', argument) else None
            if match.group("method") == "FaultPoint":
                if name is not None:
                    literal.setdefault("fault-point:" + name, []).append((path, line))
                elif DISPATCHERS.get(_dispatcher_callee(path)) != path:
                    report.error("Reachability.FaultPoint needs a literal hook name outside a hook dispatcher", path, line)
            elif name is None:
                report.error("Reachability.Sometimes needs a literal marker name", path, line)
            else:
                literal.setdefault(name, []).append((path, line))
    return literal, calls


def _dispatcher_callee(path):
    return next((callee for callee, owner in DISPATCHERS.items() if owner == path), None)


def derived_fault_points(tree, report, literal):
    """Markers implied by fault-points.json, with the hook sites that must count them."""
    registry = tree.read_json(fault_points.REGISTRY, {}) or {}
    derived = {}
    quiet = _Quiet("fault points")  # check_fault_points reports its own findings
    sites, _ = fault_points.source_hooks(tree, quiet)
    for family_key, entries in (("hooks", registry.get("hooks", [])), ("injectors", registry.get("injectors", []))):
        for entry in entries:
            name = entry.get("name")
            if not name:
                continue
            marker = "fault-point:" + name
            if marker in derived:
                report.error(f"Fault hook name {name} is registered twice; marker {marker} would be ambiguous",
                             fault_points.REGISTRY)
            evidence = entry.get("evidence", [])
            fuzz = sorted({item["fuzz"] for item in evidence if item.get("fuzz")})
            tests = [item["test"] for item in evidence if item.get("test")]
            if family_key == "hooks":
                locations = hook_sites = sites.get((entry.get("family"), name), [])
            else:
                # An injector's site is the delegate read; its literal marker is the evidence.
                locations, hook_sites = literal.get(marker, []), []
                if not locations:
                    report.error(f"Injector {name} has no Reachability.FaultPoint(\"{name}\") at its site",
                                 fault_points.REGISTRY)
            derived[marker] = {
                "name": marker, "kind": "fault-point", "derived": True,
                "paths": sorted({path for path, _ in locations}), "targets": fuzz + tests,
                "gate": "smoke",
                "reason": f"{entry.get('protocol', 'fault')} boundary registered in fault-points.json",
                "sites": hook_sites,
            }
    return derived


def check_fault_point_sites(tree, report, derived):
    """Every named hook site must count its marker: through a dispatcher or a literal just above it."""
    for marker, entry in derived.items():
        for path, line in entry.get("sites", []):
            lines = (tree.read(path) or "").splitlines()
            site = lines[line - 1] if line - 1 < len(lines) else ""
            callee = re.search(r"\b(" + "|".join(DISPATCHERS) + r")\(", site)
            dispatcher = DISPATCHERS.get(callee.group(1)) if callee else None
            if dispatcher and "Reachability.FaultPoint(" in (tree.read(dispatcher) or ""):
                continue
            above = "\n".join(lines[max(0, line - 4):line - 1])
            if f'Reachability.FaultPoint("{marker.split(":", 1)[1]}")' not in above:
                report.error(f"Fault hook site of {marker} has no marker (add Reachability.FaultPoint just above "
                             "it or call it through a marked dispatcher)", path, line)


def smoke_targets(tree):
    """Targets the PR smoke matrix of fuzz.yml runs."""
    workflow = tree.read(common.FUZZ_WORKFLOW) or ""
    match = re.search(r"^  smoke:\n(?P<body>(?:    .*\n|\n)*)", workflow, re.M)
    names = set()
    for value in re.findall(r"targets:\s+\"?([a-z0-9,\-]+)", match.group("body") if match else ""):
        names.update(filter(None, value.split(",")))
    return names


def load_registry(tree, report):
    registry = tree.read_json(REGISTRY)
    if registry is None:
        report.error(f"{REGISTRY} is missing")
        return {}, {}
    entries = {}
    for entry in common.section(registry, "markers", list, report, REGISTRY):
        name = entry.get("name", "")
        if name in entries:
            report.error(f"Marker {name} is registered twice", REGISTRY)
        entries[name] = entry
    overrides = common.section(registry, "faultPointGates", dict, report, REGISTRY)
    return entries, overrides


def check_registry(tree, report, entries, literal, derived):
    targets = common.fuzz_targets(tree)
    smoke = smoke_targets(tree)
    for name, entry in sorted(entries.items()):
        match = NAME.match(name)
        kind = entry.get("kind")
        if not match or kind not in KINDS or match.group("kind") != kind:
            report.error(f"Marker {name!r}: name must be <kind>:<id> with kind one of {', '.join(sorted(KINDS))}",
                         REGISTRY)
            continue
        if kind == "fault-point":
            report.error(f"Marker {name}: fault-point markers are derived from {fault_points.REGISTRY}", REGISTRY)
        if name not in literal:
            report.error(f"Registered marker {name} no longer exists in {' or '.join(SCANNED)}", REGISTRY)
        if entry.get("gate", "smoke") not in GATES or (
                entry.get("gate") == "advisory" and len((entry.get("gateReason") or "").strip()) < 10):
            report.error(f"Marker {name}: gate must be smoke, or advisory with a gateReason", REGISTRY)
        if len((entry.get("reason") or "").strip()) < 10:
            report.error(f"Marker {name}: state the situation it proves in 'reason'", REGISTRY)
        if not entry.get("paths"):
            report.error(f"Marker {name}: list the source files whose change declares it in 'paths'", REGISTRY)
        for path in entry.get("paths", []):
            if not tree.exists(path):
                report.error(f"Marker {name}: path {path} does not exist", REGISTRY)
        if entry.get("doc") and not tree.exists(entry["doc"].split("#")[0]):
            report.error(f"Marker {name}: doc {entry['doc']} does not exist", REGISTRY)
        if entry.get("gate", "smoke") == "smoke" and not smoke.intersection(entry.get("targets", [])):
            report.error(f"Marker {name}: a smoke-gated marker needs a target that the PR smoke matrix runs "
                         "(or gate advisory with a gateReason)", REGISTRY)
        for target in entry.get("targets", []):
            if "#" in target:
                if common.resolve_test(tree, target) is None:
                    report.error(f"Marker {name}: test {target} does not resolve", REGISTRY)
            elif target not in targets:
                report.error(f"Marker {name}: fuzz target {target} does not exist", REGISTRY)
    for name, locations in sorted(literal.items()):
        if name.startswith("fault-point:"):
            if name not in derived:
                path, line = locations[0]
                report.error(f"Marker {name} names no hook registered in {fault_points.REGISTRY}", path, line)
        elif name not in entries:
            path, line = locations[0]
            report.error(f"Marker {name} is not registered in {REGISTRY}", path, line)


def all_markers(tree, report):
    """Static check; return {name: entry} for registered and derived markers."""
    literal, _ = code_markers(tree, report)
    entries, overrides = load_registry(tree, report)
    derived = derived_fault_points(tree, report, literal)
    for name, override in overrides.items():
        marker = name if name.startswith("fault-point:") else "fault-point:" + name
        if marker not in derived:
            report.error(f"faultPointGates names unknown fault point {name}", REGISTRY)
        elif override.get("gate") not in GATES or len((override.get("reason") or "").strip()) < 10:
            report.error(f"faultPointGates.{name}: give a gate (smoke/advisory) and a reason", REGISTRY)
        else:
            derived[marker]["gate"] = override["gate"]
            derived[marker]["gateReason"] = override["reason"]
    check_fault_point_sites(tree, report, derived)
    check_registry(tree, report, entries, literal, derived)
    return {**derived, **entries}


class _Quiet(common.Report):
    """A report that only collects: findings another check (or another revision) owns."""

    def error(self, message, path=None, line=None):
        self.errors.append(message)

    def warning(self, message, path=None, line=None):
        self.warnings.append(message)


# --- diff mode -------------------------------------------------------------

def declared_for_diff(base, head, markers, report):
    """Markers the diff declares, mapped to why; also checks api: markers for API changes."""
    base_tree = common.Tree(base)
    changed = set(common.changed_files(base, head.rev))
    base_entries = {entry.get("name"): entry for entry in (base_tree.read_json(REGISTRY, {}) or {}).get("markers", [])
                    if isinstance(entry, dict)}
    quiet = _Quiet("base")
    base_derived = derived_fault_points(base_tree, quiet, code_markers(base_tree, quiet)[0])
    declared = {}
    for name, entry in markers.items():
        if not entry.get("derived") and base_entries.get(name) != entry:
            declared.setdefault(name, []).append("new or changed registry entry")
        hit = sorted(changed.intersection(entry.get("paths", [])))
        if hit:
            declared.setdefault(name, []).append("changed " + ", ".join(hit))
        if entry.get("derived") and name not in base_derived:
            declared.setdefault(name, []).append("fault point added")
    for marker, where in api_changes(base_tree, head, changed):
        declared.setdefault(marker, []).append(where)
        if marker not in markers:
            report.error(f"Public API {marker[4:]} changed without a registered {marker} marker; drive it from a "
                         f"fuzz target and register it in {REGISTRY}", where.split(" ")[-1])
    return declared


def api_changes(base, head, changed):
    """Yield (api marker, reason) for public API members added or changed by the diff."""
    paths = [path for path in sorted(changed) if path.startswith("LiteDB/") and path.endswith(".cs")]
    before, after = _public_surface(base, paths), _public_surface(head, paths)
    base_types = _public_types(base, base.paths())
    enums = {match.group("name") for path in paths
             for match in TYPE.finditer(common.blank_code(head.read(path) or "")) if match.group("kind") == "enum"}
    for (type_name, member), signatures in sorted(after.items()):
        if type_name in API_TYPES and signatures != before.get((type_name, member), set()):
            yield f"api:{type_name}.{member}", f"changed member in {sorted(signatures)[0][0]}"
    for type_name, path in sorted(_public_types(head, paths).items()):
        if type_name in base_types or type_name in API_TYPES or type_name in enums:
            continue
        members = sorted(member for (owner, member) in after if owner == type_name)
        for member in members or [None]:
            yield (f"api:{type_name}.{member}" if member else f"api:{type_name}"), f"new public type in {path}"


def _public_types(tree, paths):
    types = {}
    for path in paths:
        if path.startswith("LiteDB/") and path.endswith(".cs"):
            for match in TYPE.finditer(common.blank_code(tree.read(path) or "")):
                types.setdefault(match.group("name"), path)
    return types


def _public_surface(tree, paths):
    """{(type, member): {(path, normalized declaration)}} for interfaces and public type members."""
    surface = {}
    for path in paths:
        text = common.blank_code(tree.read(path) or "")
        for match in TYPE.finditer(text):
            if match.group("kind") == "enum":
                continue
            start = text.find("{", match.end())
            if start < 0:
                continue
            body = re.sub(r"^\s*#.*$", "", text[start + 1:common.matching(text, start, "{", "}") - 1], flags=re.M)
            interface = match.group("kind") == "interface"
            for declaration in _top_level_declarations(body):
                if not interface and not re.match(r"\s*(?:\[[^\]]*\]\s*)*public\b", declaration):
                    continue
                if re.search(r"\b(?:class|interface|struct|record|enum|delegate)\s", declaration.split("(")[0]):
                    continue
                member = MEMBER.search(declaration.split("=>")[0])
                if member:
                    name = member.group("name") or member.group("field")
                    name = "Item" if name == "this" else name
                    surface.setdefault((match.group("name"), name), set()).add(
                        (path, " ".join(declaration.split())))
    return surface


def _top_level_declarations(body):
    """Member declarations at nesting depth zero (signature up to its body or ';')."""
    declarations, depth, current = [], 0, ""
    for char in body:
        if depth == 0:
            if char in "{;" or (char == "=" and current.rstrip().endswith(")")):
                if current.strip():
                    declarations.append(current.strip())
                current = ""
            else:
                current += char
        depth += char == "{"
        depth -= char == "}"
    return declarations


# --- run mode --------------------------------------------------------------

def aggregate(directories):
    """{marker: {"hits": n, "targets": {target: n}}} over every markers.json; plus run count."""
    totals, runs = {}, 0
    for directory in directories:
        for path in sorted(Path(directory).rglob("markers.json")):
            try:
                data = common.load_json_file(path)
            except ValueError:
                continue
            runs += 1
            for name, count in (data.get("hits") or {}).items():
                item = totals.setdefault(name, {"hits": 0, "targets": {}})
                item["hits"] += count
                target = data.get("target", "?")
                item["targets"][target] = item["targets"].get(target, 0) + count
    return totals, runs


def report_runs(markers, totals, runs, declared, report, output):
    rows = ["| Marker | Hits | Targets | Gate | Declared by the diff |", "| --- | ---: | --- | --- | --- |"]
    names = sorted(set(markers) | set(totals))
    for name in names:
        entry, item = markers.get(name, {}), totals.get(name, {"hits": 0, "targets": {}})
        targets = ", ".join(f"{target} {count}" for target, count in sorted(item["targets"].items()))
        rows.append(f"| `{name}` | {item['hits']} | {targets} | {entry.get('gate', 'smoke')} | "
                    f"{'; '.join(declared.get(name, []))} |")
    never = [name for name in names if totals.get(name, {}).get("hits", 0) == 0]
    report.section(f"Aggregated {runs} markers.json file(s).\n\n" + "\n".join(rows))
    report.section("Never hit:\n\n" + ("\n".join(f"- `{name}`" for name in never) or "- none"))
    if runs == 0:
        report.error("No markers.json found under the run directories; the campaign recorded no reachability")
    for name in sorted(declared):
        if totals.get(name, {}).get("hits", 0):
            continue
        gate = markers.get(name, {}).get("gate", "smoke")
        message = f"Marker {name} is declared by this diff ({'; '.join(declared[name])}) but the campaign never hit it"
        if gate == "advisory":
            report.warning(message + f" (advisory: {markers[name].get('gateReason', '')})")
        else:
            report.error(message + "; extend a fuzz target so it reaches the situation")
    for name in sorted(set(totals) - set(markers)):
        report.warning(f"Hit marker {name} is not registered (stale binaries or registry)")
    if output:
        Path(output).write_text(json.dumps({
            "schemaVersion": 1, "runs": runs,
            "markers": {name: {"hits": totals.get(name, {}).get("hits", 0),
                               "targets": totals.get(name, {}).get("targets", {}),
                               "kind": markers.get(name, {}).get("kind"),
                               "gate": markers.get(name, {}).get("gate", "smoke"),
                               "declared": declared.get(name, [])} for name in names},
            "neverHit": never,
            "declaredUnhit": sorted(name for name in declared if not totals.get(name, {}).get("hits", 0)),
        }, indent=2) + "\n", encoding="utf-8")


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--base", help="Declare the markers of the diff since this revision")
    parser.add_argument("--head", default="HEAD")
    parser.add_argument("--runs", nargs="+", help="Fuzz artifact directories holding markers.json files")
    parser.add_argument("--output", help="Write the aggregated reachability as JSON (run mode)")
    args = parser.parse_args(argv)
    head = common.Tree(args.head)
    report = common.Report("Reachability markers")
    try:
        markers = all_markers(head, report)
        declared = declared_for_diff(args.base, head, markers, report) if args.base else {}
    except common.MalformedJson as error:
        report.error(str(error), REGISTRY)
        markers, declared = {}, {}
    if args.runs:
        totals, runs = aggregate(args.runs)
        report_runs(markers, totals, runs, declared, report, args.output)
    elif declared:
        report.section("Markers declared by this diff:\n\n" + "\n".join(
            f"- `{name}`: {'; '.join(reasons)}" for name, reasons in sorted(declared.items())))
    return report.finish()


if __name__ == "__main__":
    sys.exit(main())
