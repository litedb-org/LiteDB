"""Per-PR fuzz target selection: obligation map first, coverage second, all targets otherwise.

For every changed file, in this order (each decision is recorded per file):

1. Obligation map (.github/safety/fuzz-obligations.json). Every obligation whose
   `paths` globs match the file, and whose `patterns` regexes (when given) match an
   added or removed non-comment line of that file, contributes its `targets`.
   Obligations with `"decides": false` (cross-cutting patterns such as a new wait or
   `finally`) add targets but leave the file undecided, so its own subsystem is still
   selected. `alwaysForLiteDB` is added for any change under LiteDB/.
2. Coverage (.github/safety/fuzz-coverage-map.json, see generate_fuzz_coverage_map.py):
   a file no deciding obligation matched gets every target whose recorded coverage
   includes it. Files matching the map's `ignore` globs (documentation, unrelated
   tools) are `ignored` instead.
3. Every known target when neither decides. Coverage-only selection misses exactly
   the new code a change adds, so an undecided file never selects nothing.

Known targets are the `Name =>` declarations in LiteDB.Fuzz/Targets plus the map's
`pendingTargets` (being added by other changes). The union is intersected with the
targets available in this tree; required but unavailable targets are listed under
`missing` with a warning, never dropped silently (exit 1 only with --strict).

The obligation map schema:

  {"schemaVersion": 1, "description": "...",
   "alwaysForLiteDB": [target], "pendingTargets": [target], "pendingPaths": [glob],
   "ignore": [glob],
   "obligations": [{"id": "kebab-case", "kind": one of KINDS, "reason": "why (plan row)",
                    "paths": [glob with **], "patterns": [regex], "decides": true,
                    "targets": [target | "@all" | "@fuzz-source-users"], "gap": "..."}]}

`@all` selects every known target; `@fuzz-source-users` selects the targets declared
in the changed file plus those whose LiteDB.Fuzz/Targets sources use a type it
declares (transitively). --validate checks the map (unique ids, known kinds and
targets, globs that match a file or are pending, compiling regexes, ignore globs
that never hide LiteDB/) and the coverage map, and is run by the Safety policy job.

`--all` (a critical change, see classify_critical.py) selects every known target whatever
the decisions say; the decisions are still recorded.

Counts come from the map's `prCounts`: {selected: n, allTargets: n, caps: {target: {count,
smokeLeg, reason}}, uncapped: {target: reason}}. A selected target runs `selected` steps
(`allTargets` when every target is selected), capped at its `caps` count: min(cap, mode
count). The selection is emitted as count groups, one fuzz invocation each. A cap mirrors
the PR smoke leg it names (`smokeLeg`: a leg of fuzz.yml's smoke matrix on the runner of
the pr-selected job that runs the target; null when no such leg exists, and then the
reason says why), and --validate fails when the two differ. Targets whose source starts
child processes or drives the concurrency explorer must be listed under `caps` or, with a
reason, under `uncapped`.

The seed is fixed per PR so reruns are comparable: 2947000 + PR number (2947 without
--pr). The output JSON is {schemaVersion, base, head, seed, targets, missing,
allTargets, groups: [{count, targets}], decisions: [{file, decidedBy, obligations, targets}],
reasons}.
"""
import argparse
import json
import os
import re
import subprocess
import sys

import safety_common as common

OBLIGATIONS = f"{common.SAFETY_DIR}/fuzz-obligations.json"
COVERAGE_MAP = f"{common.SAFETY_DIR}/fuzz-coverage-map.json"
PRODUCT_ROOT = "LiteDB/"
SEED_BASE, DEFAULT_SEED = 2947000, 2947
ALL, SOURCE_USERS = "@all", "@fuzz-source-users"
TOKENS = {ALL, SOURCE_USERS}
KINDS = {"lock-wait", "teardown", "public-api", "fault-point", "callback", "transaction", "shared", "storage",
         "query", "index", "bson", "mapper", "rebuild", "vector", "compatibility", "safety-machinery", "fuzz-harness"}
TOP_KEYS = {"schemaVersion", "description", "alwaysForLiteDB", "pendingTargets", "pendingPaths", "prCounts", "ignore",
            "obligations"}
COUNT_KEYS = {"selected", "allTargets", "caps", "uncapped"}
CAP_KEYS = {"count", "smokeLeg", "reason"}
PR_JOB = "pr-selected"
# Starts child processes or drives the concurrency explorer: such a target needs a cap decision.
NEEDS_CAP = re.compile(r"\bProcess\.Start\b|\bProcessStartInfo\b|\bFuzzExplorerHost\b|\bExplorerRun\.Execute\b")
OBLIGATION_KEYS = {"id", "kind", "reason", "paths", "patterns", "decides", "targets", "gap"}
ID = re.compile(r"[a-z0-9]+(?:-[a-z0-9]+)*\Z")
COMMENT = re.compile(r"\s*(?://|/\*|\*)")
TYPE = re.compile(r"\b(?:class|struct|record|interface|enum)\s+([A-Za-z_]\w*)")
TARGET_NAME = re.compile(r'\bstring\s+Name\s*=>\s*"([^"]+)"')


# --- the obligation map ------------------------------------------------------

def load_json(path, report, required=True):
    if not os.path.isfile(path):
        if required:
            report.error("The file is missing", path)
        return None
    try:
        return common.load_json_file(path)
    except ValueError as error:
        report.error(f"Not valid JSON: {error}", path)
        return None


def strings(policy, key, report, path):
    values = policy.get(key, [])
    if not isinstance(values, list) or not all(isinstance(value, str) and value for value in values):
        report.error(f"'{key}' must be an array of non-empty strings", path)
        return []
    return values


def validate(policy, tree, declared, report, path=OBLIGATIONS):
    """Check the obligation map; return the obligations with compiled globs and patterns."""
    if not isinstance(policy, dict):
        report.error(f"the obligation map must be a JSON object", path)
        return []
    if policy.get("schemaVersion") != 1:
        report.error(f"schemaVersion must be 1", path)
    if len(str(policy.get("description") or "").strip()) < 20:
        report.error(f"describe the selection rules in 'description'", path)
    for key in sorted(set(policy) - TOP_KEYS):
        report.error(f"unknown key '{key}'", path)
    pending = strings(policy, "pendingTargets", report, path)
    known = set(declared) | set(pending)
    for name in sorted(set(pending) & set(declared)):
        report.warning(f"pending target {name} now exists in LiteDB.Fuzz/Targets; remove it from pendingTargets", path)
    for name in strings(policy, "alwaysForLiteDB", report, path):
        if name not in known:
            report.error(f"alwaysForLiteDB names unknown target '{name}'", path)
    files = tree.paths()
    pending_paths = strings(policy, "pendingPaths", report, path)
    for glob in strings(policy, "ignore", report, path):
        if _bad_glob(glob, path, report):
            continue
        hidden = [name for name in files if name.startswith(PRODUCT_ROOT) and common.glob_regex(glob).match(name)]
        if hidden:
            report.error(f"ignore glob '{glob}' matches product code ({hidden[0]}); LiteDB/ changes are never ignored", path)
    obligations = common.section(policy, "obligations", list, report, path)
    seen, compiled = set(), []
    for index, item in enumerate(obligations):
        compiled_item = _validate_obligation(item, index, seen, known, files, pending_paths, report, path)
        if compiled_item:
            compiled.append(compiled_item)
    return compiled


def _validate_obligation(item, index, seen, known, files, pending_paths, report, path):
    label = item.get("id") if isinstance(item.get("id"), str) else f"obligations[{index}]"
    errors = len(report.errors)
    for key in sorted(set(item) - OBLIGATION_KEYS):
        report.error(f"{label}: unknown key '{key}'", path)
    if not isinstance(item.get("id"), str) or not ID.match(item["id"]):
        report.error(f"{label}: id must be kebab-case", path)
    elif item["id"] in seen:
        report.error(f"obligation id {label} is used twice", path)
    seen.add(item.get("id"))
    if item.get("kind") not in KINDS:
        report.error(f"{label}: kind must be one of {', '.join(sorted(KINDS))}", path)
    if len(str(item.get("reason") or "").strip()) < 10:
        report.error(f"{label}: say why (cite the plan row or rule)", path)
    if "decides" in item and not isinstance(item["decides"], bool):
        report.error(f"{label}: 'decides' must be true or false", path)
    if "gap" in item and len(str(item["gap"] or "").strip()) < 10:
        report.error(f"{label}: describe the gap", path)
    paths = item.get("paths")
    if not isinstance(paths, list) or not paths or not all(isinstance(glob, str) and glob for glob in paths):
        report.error(f"{label}: 'paths' must be a non-empty array of globs", path)
        paths = []
    globs = []
    for glob in paths:
        if _bad_glob(glob, path, report, label):
            continue
        regex = common.glob_regex(glob)
        globs.append(regex)
        if glob not in pending_paths and not any(regex.match(name) for name in files):
            report.error(f"{label}: glob '{glob}' matches no file (a typo? list a path another change "
                         f"adds under pendingPaths)", path)
    patterns = []
    raw_patterns = item.get("patterns", [])
    if not isinstance(raw_patterns, list) or not all(isinstance(value, str) and value for value in raw_patterns):
        report.error(f"{label}: 'patterns' must be an array of regexes", path)
        raw_patterns = []
    for pattern in raw_patterns:
        try:
            patterns.append(re.compile(pattern))
        except re.error as error:
            report.error(f"{label}: pattern {pattern!r} does not compile: {error}", path)
    targets = item.get("targets")
    if not isinstance(targets, list) or not targets or not all(isinstance(name, str) for name in targets):
        report.error(f"{label}: 'targets' must be a non-empty array of target names", path)
        targets = []
    for name in targets:
        if name not in known and name not in TOKENS:
            report.error(f"{label}: unknown target '{name}'", path)
    if len(report.errors) > errors:
        return None
    return {**item, "globs": globs, "compiled": patterns, "decides": item.get("decides", True)}


def validate_counts(policy, tree, declared, known, report, path=OBLIGATIONS):
    """Check `prCounts`; return {"selected", "allTargets", "caps": {target: count}} or None."""
    counts = policy.get("prCounts")
    if not isinstance(counts, dict):
        report.error("'prCounts' must be an object {selected, allTargets, caps, uncapped}", path)
        return None
    errors = len(report.errors)
    for key in sorted(set(counts) - COUNT_KEYS):
        report.error(f"prCounts: unknown key '{key}'", path)
    for key in ("selected", "allTargets"):
        if not _positive(counts.get(key)):
            report.error(f"prCounts.{key} must be a positive integer", path)
    caps, uncapped = counts.get("caps", {}), counts.get("uncapped", {})
    if not isinstance(caps, dict) or not isinstance(uncapped, dict):
        report.error("prCounts.caps and prCounts.uncapped must be objects keyed by target", path)
        return None
    legs, runner = smoke_legs(tree.read(common.FUZZ_WORKFLOW) or "")
    for name, cap in sorted(caps.items()):
        _validate_cap(name, cap, known, legs, runner, report, path)
    for name, reason in sorted(uncapped.items()):
        if name not in known:
            report.error(f"prCounts.uncapped names unknown target '{name}'", path)
        if name in caps:
            report.error(f"prCounts: {name} is both capped and uncapped", path)
        if len(str(reason or "").strip()) < 20:
            report.error(f"prCounts.uncapped.{name}: say why the target runs the full count", path)
    for name, source in sorted(declared.items()):
        if name not in caps and name not in uncapped and NEEDS_CAP.search(common.blank_code(tree.read(source) or "")):
            report.error(f"target {name} starts child processes or drives the concurrency explorer ({source}); "
                         f"cap its PR count under prCounts.caps or say under prCounts.uncapped why it runs in full",
                         path)
    if len(report.errors) > errors:
        return None
    return {"selected": counts["selected"], "allTargets": counts["allTargets"],
            "caps": {name: cap["count"] for name, cap in caps.items()}}


def _validate_cap(name, cap, known, legs, runner, report, path):
    label = f"prCounts.caps.{name}"
    if name not in known:
        report.error(f"{label}: unknown target '{name}'", path)
    if not isinstance(cap, dict):
        report.error(f"{label} must be an object {{count, smokeLeg, reason}}", path)
        return
    for key in sorted(set(cap) - CAP_KEYS):
        report.error(f"{label}: unknown key '{key}'", path)
    if not _positive(cap.get("count")):
        report.error(f"{label}: count must be a positive integer", path)
    if len(str(cap.get("reason") or "").strip()) < 20:
        report.error(f"{label}: say why the target is capped", path)
    if "smokeLeg" not in cap:
        report.error(f"{label}: name the smoke leg the cap mirrors in 'smokeLeg' (null when there is none)", path)
        return
    running = sorted(leg for leg, item in legs.items() if item["os"] == runner and name in item["targets"])
    leg = cap["smokeLeg"]
    if leg is None:
        if running:
            first = running[0]
            report.error(f"{label}: smoke leg {first} runs {name} on {runner} with count {legs[first]['count']}; "
                         f"set smokeLeg to it and mirror its count", path)
        return
    item = legs.get(leg)
    if item is None:
        report.error(f"{label}: smokeLeg '{leg}' is not a leg of the smoke matrix in {common.FUZZ_WORKFLOW}", path)
    elif item["os"] != runner or name not in item["targets"]:
        report.error(f"{label}: smoke leg {leg} must run {name} on {runner}, the runner of the {PR_JOB} job "
                     f"(it runs {', '.join(item['targets'])} on {item['os']})", path)
    elif item["count"] != cap.get("count"):
        report.error(f"{label}: count {cap.get('count')} differs from smoke leg {leg} (count {item['count']}) in "
                     f"{common.FUZZ_WORKFLOW}; keep the two equal", path)


def _positive(value):
    return isinstance(value, int) and not isinstance(value, bool) and value > 0


def smoke_legs(workflow):
    """({leg name: {os, targets, count}} of fuzz.yml's smoke matrix, runs-on of the pr-selected job)."""
    legs = {}
    job = re.search(r"^  smoke:\n(?P<body>(?:    .*\n|\n)*)", workflow, re.M)
    include = re.search(r"\binclude:\n(?P<items>(?:(?: {8,}.*)?\n)*)", job.group("body") if job else "")
    for chunk in re.split(r"^ +- name:", include.group("items") if include else "", flags=re.M)[1:]:
        fields = dict(re.findall(r"^\s*(os|targets|count):\s*\"?([^\"\n#]*?)\"?\s*$", "name:" + chunk, re.M))
        name = chunk.split("\n", 1)[0].strip().strip('"')
        count = fields.get("count", "")
        legs[name] = {"os": fields.get("os", ""), "targets": [item for item in fields.get("targets", "").split(",") if item],
                      "count": int(count) if count.isdigit() else None}
    runner = re.search(rf"^  {PR_JOB}:\n(?:    .*\n|\n)*?    runs-on:\s*(\S+)", workflow, re.M)
    return legs, runner.group(1) if runner else None


def count_groups(targets, all_targets, counts):
    """[{count, targets}] for the selected targets: min(cap, mode count), larger counts first."""
    mode = counts["allTargets"] if all_targets else counts["selected"]
    groups = {}
    for name in sorted(targets):
        groups.setdefault(min(counts["caps"].get(name, mode), mode), []).append(name)
    return [{"count": count, "targets": names} for count, names in sorted(groups.items(), reverse=True)]


def _bad_glob(glob, path, report, label=None):
    prefix = f"{label}: " if label else ""
    if "\\" in glob or glob.startswith(("/", "./")) or "//" in glob:
        report.error(f"{prefix}glob '{glob}' must be a repository-relative path with forward slashes", path)
        return True
    return False


def check_coverage_map(coverage, known, tree, report, path=COVERAGE_MAP):
    """Consistency of the coverage map with this tree; staleness is a warning (refresh the map)."""
    if coverage is None:
        report.warning("The coverage map is missing; every file without a deciding obligation selects all targets", path)
        return {}
    targets = coverage.get("targets") if isinstance(coverage, dict) else None
    if coverage.get("schemaVersion") != 1 or not isinstance(targets, dict) or not all(
            isinstance(files, list) for files in targets.values()):
        report.error(f"needs schemaVersion 1 and 'targets' mapping each target to a file list", path)
        return {}
    files = set(tree.paths())
    unknown = sorted(set(targets) - set(known))
    if unknown:
        report.warning(f"targets no longer known: {', '.join(unknown)}; regenerate the map", path)
    absent = sorted(set(known) - set(targets))
    if absent:
        report.section("Targets without recorded coverage (selected only through obligations or the all-targets "
                       "fallback until the map is regenerated): " + ", ".join(f"`{name}`" for name in absent))
    stale = sorted({name for names in targets.values() for name in names if name not in files})
    if stale:
        report.warning(f"{len(stale)} recorded files no longer exist (first: {stale[0]}); regenerate the map", path)
    by_file = {}
    for target, names in targets.items():
        for name in names:
            by_file.setdefault(name, set()).add(target)
    return by_file


# --- the diff ------------------------------------------------------------------

def parse_diff(text):
    """Return {path: [changed line text]} for added and removed lines of a unified diff."""
    lines, old, path, in_hunk = {}, None, None, False
    for raw in text.splitlines():
        if raw.startswith("diff --git "):
            old, path, in_hunk = None, None, False
        elif not in_hunk and raw.startswith("--- "):
            old = None if raw[4:] == "/dev/null" else _strip_prefix(raw[4:])
        elif not in_hunk and raw.startswith("+++ "):
            path = old if raw[4:] == "/dev/null" else _strip_prefix(raw[4:])
            lines.setdefault(path, [])
        elif raw.startswith("@@"):
            in_hunk = True
        elif in_hunk and path and raw[:1] in "+-":
            lines[path].append(raw[1:])
    return lines


def _strip_prefix(name):
    name = name.split("\t")[0]
    return name[2:] if name[:2] in ("a/", "b/") else name


def git_changes(base, head):
    """({path: status}, {path: [changed lines]}) between two revisions."""
    changes = common.changed_files(base, head)
    diff = common.git("-c", "core.quotePath=false", "diff", "-U0", "--no-renames", "--no-color", base, head)
    return changes, parse_diff(diff)


def resolve_base(base, head):
    """The merge-base of the given revision and head, so only the PR's own changes count."""
    try:
        return common.git("merge-base", base, head).strip()
    except subprocess.CalledProcessError:
        print(f"WARNING: no merge-base between {base} and {head} (shallow clone?); diffing against {base}")
        return common.git("rev-parse", base).strip()


# --- selection -----------------------------------------------------------------

def source_users(tree, path, base_tree=None):
    """Targets declared in `path` plus targets whose LiteDB.Fuzz/Targets sources use a type it declares."""
    sources = {name: tree.read(name) or "" for name in tree.paths()
               if name.startswith(common.FUZZ_TARGETS_DIR) and name.endswith(".cs")}
    text = tree.read(path) or (base_tree.read(path) if base_tree else None) or ""
    found = set(TARGET_NAME.findall(text))
    types, seen = set(TYPE.findall(common.blank_code(text))), {path}
    while types:
        words = re.compile(r"\b(?:" + "|".join(sorted(map(re.escape, types))) + r")\b")
        types = set()
        for name, source in sorted(sources.items()):
            code = common.blank_code(source)
            if name not in seen and words.search(code):
                seen.add(name)
                found.update(TARGET_NAME.findall(source))
                types.update(TYPE.findall(code))
    return found


def matches(obligation, path, lines):
    """True when the obligation applies to this file's change; None lines means the diff is unknown."""
    if not any(glob.match(path) for glob in obligation["globs"]):
        return False
    if not obligation["compiled"] or lines is None:
        return True
    code = [line for line in lines if not COMMENT.match(line)]
    return any(pattern.search(line) for pattern in obligation["compiled"] for line in code)


def select(changes, diff_lines, policy, obligations, coverage, known, tree, base_tree=None):
    """Return (selected target set, all-targets flag, decisions, reasons)."""
    ignore = [(glob, common.glob_regex(glob)) for glob in policy.get("ignore", [])]
    always = list(policy.get("alwaysForLiteDB", []))
    selected, reasons, decisions, everything = set(), {}, [], False

    def add(file, names, why):
        for name in names:
            selected.add(name)
            reasons.setdefault(name, set()).add(f"{why}: {file}")

    for path in sorted(changes):
        lines = None if diff_lines is None else diff_lines.get(path, [])
        file_targets, ids, decided = set(), [], False
        for obligation in obligations:
            if not matches(obligation, path, lines):
                continue
            names = set()
            for name in obligation["targets"]:
                if name == ALL:
                    names.update(known)
                    everything = True
                elif name == SOURCE_USERS:
                    names.update(source_users(tree, path, base_tree))
                else:
                    names.add(name)
            if not names:
                continue
            ids.append(obligation["id"])
            decided = decided or obligation["decides"]
            file_targets |= names
            add(path, names, f"obligation {obligation['id']}")
        if path.startswith(PRODUCT_ROOT) and always:
            ids.append("alwaysForLiteDB")
            file_targets |= set(always)
            add(path, always, "alwaysForLiteDB")
        rule = next((glob for glob, regex in ignore if regex.match(path)), None)
        if decided:
            decided_by = "obligation"
        elif rule and not path.startswith(PRODUCT_ROOT):
            decided_by = "ignored"
            ids.append(f"ignore:{rule}")
        elif path in coverage:
            decided_by = "coverage"
            file_targets |= coverage[path]
            add(path, coverage[path], "coverage")
        else:
            decided_by = "fallback-all"
            everything = True
            file_targets |= set(known)
            add(path, known, "fallback-all")
        decision = {"file": path, "decidedBy": decided_by, "obligations": ids, "targets": sorted(file_targets)}
        if lines is None and any(item["compiled"] for item in obligations if item["id"] in ids):
            decision["patternsEvaluated"] = False
        decisions.append(decision)
    return selected, everything, decisions, {name: sorted(why) for name, why in sorted(reasons.items())}


def parse_available(value, tree):
    if value is None:
        return set(common.fuzz_targets(tree))
    if os.path.isfile(value):
        with open(value, encoding="utf-8") as handle:
            value = handle.read()
    return {name.strip() for name in re.split(r"[,\s]+", value) if name.strip()}


def read_changed_files(path):
    with open(path, encoding="utf-8-sig") as handle:
        names = [line.strip() for line in handle if line.strip() and not line.startswith("#")]
    return {name.split("\t")[-1]: (name.split("\t")[0][:1] if "\t" in name else "M") for name in names}


def summarize(result, report):
    rows = ["| File | Decided by | Obligations | Targets |", "| --- | --- | --- | --- |"]
    for item in result["decisions"]:
        shown = ", ".join(item["targets"]) if len(item["targets"]) <= 12 else f"{len(item['targets'])} targets"
        rows.append(f"| `{item['file']}` | {item['decidedBy']} | {', '.join(item['obligations'])} | {shown} |")
    header = (f"Seed {result['seed']}; {len(result['targets'])} target(s)"
              + (" (all targets: an undecided file)" if result["allTargets"] else "")
              + f": {', '.join(result['targets']) or 'none'}")
    groups = "; ".join(f"{', '.join(group['targets'])} x {group['count']}" for group in result["groups"])
    report.section(header + (f"\n\nCounts: {groups}" if groups else "") + "\n\n" + "\n".join(rows))


def write_github_output(result):
    target = os.environ.get("GITHUB_OUTPUT")
    groups = " ".join(f"{group['count']}:{','.join(group['targets'])}" for group in result["groups"])
    lines = [f"targets={','.join(result['targets'])}", f"seed={result['seed']}", f"count-groups={groups}",
             f"all-targets={'true' if result['allTargets'] else 'false'}",
             f"missing={','.join(result['missing'])}"]
    if target:
        with open(target, "a", encoding="utf-8") as handle:
            handle.write("\n".join(lines) + "\n")
    else:
        print("\n".join(lines))


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0], epilog=__doc__.split("\n\n", 1)[1],
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--validate", action="store_true", help="Only validate the obligation and coverage maps")
    parser.add_argument("--base", help="Diff base; the merge-base of this revision and --head is used (default origin/dev)")
    parser.add_argument("--head", default="HEAD", help="Diff head revision (default HEAD)")
    parser.add_argument("--changed-files", help="File listing changed paths (one per line) instead of git")
    parser.add_argument("--diff", help="Unified diff file whose +/- lines feed the obligation patterns")
    parser.add_argument("--obligations", default=None, help=f"Obligation map (default {OBLIGATIONS})")
    parser.add_argument("--coverage-map", default=None, help=f"Coverage map (default {COVERAGE_MAP})")
    parser.add_argument("--available", help="Targets that exist in this build: comma list or file "
                                            "(default: the Name declarations in LiteDB.Fuzz/Targets)")
    parser.add_argument("--pr", type=int, help=f"PR number; the seed is {SEED_BASE} + PR (default seed {DEFAULT_SEED})")
    parser.add_argument("--output", help="Write the selection JSON here")
    parser.add_argument("--github-output", action="store_true", help="Append targets=, seed=, count-groups= "
                                                                       "(space-separated count:target,... groups), "
                                                                       "all-targets= and missing= to $GITHUB_OUTPUT")
    parser.add_argument("--strict", action="store_true", help="Exit 1 when a required target is unavailable")
    parser.add_argument("--all", action="store_true", help="Select every known target (a critical change)")
    args = parser.parse_args(argv)
    root = common.repo_root()
    tree = common.Tree(common.WORKTREE)
    report = common.Report("Fuzz target selection" if not args.validate else "Fuzz obligation map")
    obligations_path = args.obligations or os.path.join(root, OBLIGATIONS)
    coverage_path = args.coverage_map or os.path.join(root, COVERAGE_MAP)
    sources = common.fuzz_targets(tree)
    declared = set(sources)
    policy = load_json(obligations_path, report) or {}
    obligations = validate(policy, tree, declared, report, OBLIGATIONS)
    known = sorted(declared | set(policy.get("pendingTargets", []) if isinstance(policy, dict) else []))
    counts = validate_counts(policy, tree, sources, known, report) if isinstance(policy, dict) else None
    coverage = check_coverage_map(load_json(coverage_path, report, required=False), known, tree, report)
    if args.validate or report.errors:
        caps = len(counts["caps"]) if counts else 0
        report.section(f"{len(obligations)} obligations; {len(known)} known targets; {caps} PR count caps.")
        return report.finish()

    base_tree = None
    if args.changed_files:
        changes = read_changed_files(args.changed_files)
        diff_lines = None
        base, head = None, None
    elif args.diff:
        changes, diff_lines, base, head = None, None, None, None
    else:
        head = common.git("rev-parse", args.head).strip()
        base = resolve_base(args.base or "origin/dev", head)
        changes, diff_lines = git_changes(base, head)
        base_tree = common.Tree(base)
    if args.diff:
        with open(args.diff, encoding="utf-8-sig") as handle:
            diff_lines = parse_diff(handle.read())
        if changes is None:
            changes = {name: "M" for name in diff_lines}
        for name in changes:  # a listed file without hunks (binary, mode change) has no changed lines
            diff_lines.setdefault(name, [])

    available = parse_available(args.available, tree)
    required, everything, decisions, reasons = select(changes, diff_lines, policy, obligations, coverage, known,
                                                      tree, base_tree)
    if args.all:
        everything = True
        for name in known:
            required.add(name)
            reasons.setdefault(name, []).append("critical change: all targets")
    seed = SEED_BASE + args.pr if args.pr is not None else DEFAULT_SEED
    targets = sorted(required & available)
    result = {"schemaVersion": 1, "base": base, "head": head, "seed": seed,
              "targets": targets, "missing": sorted(required - available), "allTargets": everything,
              "groups": count_groups(targets, everything, counts), "decisions": decisions, "reasons": reasons}
    for name in result["missing"]:
        report.warning(f"Required fuzz target {name} is not available in this tree; it was not run "
                       f"({'; '.join(reasons.get(name, ['fallback-all'])[:3])})")
    summarize(result, report)
    if args.output:
        with open(args.output, "w", encoding="utf-8", newline="\n") as handle:
            json.dump(result, handle, indent=2)
            handle.write("\n")
    if args.github_output:
        write_github_output(result)
    counts = {}
    for item in decisions:
        counts[item["decidedBy"]] = counts.get(item["decidedBy"], 0) + 1
    print(f"Selected {len(result['targets'])} target(s), seed {seed}, all targets: {str(everything).lower()}; "
          f"{len(decisions)} file(s): " + ", ".join(f"{count} {kind}" for kind, count in sorted(counts.items()))
          + (f"; missing: {', '.join(result['missing'])}" if result["missing"] else ""))
    code = report.finish()
    return 1 if code or (args.strict and result["missing"]) else 0


if __name__ == "__main__":
    sys.exit(main())
