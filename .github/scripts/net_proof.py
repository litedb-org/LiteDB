"""Net proofs: a general safety net fires at a real known-bad state and stays quiet at its fix.

A regression proof (regression_proof.py) shows that a FIX is covered by a black-box
ReproRunner repro. A net proof shows that a general SAFETY NET (an oracle, the
wait-for graph, a fuzz target, a lint) detects a defect it was not written for:
the net fires at the defect's known-bad commit and stays quiet at its fix.
Entries live in .github/safety/net-proofs.json; known-bad states use the same
provenance as regression proofs (proof_provenance.py, including `repository`
for a fork's pull request).

`run` checks out both commits as detached worktrees, applies the net's overlay
(cherry-picked commits, patches, adapter directories copied from
tools/net-proofs/adapters/), probes the capabilities each tree has before and
after the overlay, and reports `not-applicable` with the missing capability names
when a required one is absent: such a proof is never skipped silently and never
counted as passing. Otherwise it builds both trees, runs the net's command under a
hard wall-clock limit as the entry's evidence class requires, and writes a
result JSON. `validate` checks the ledger offline (and provenance with
--provenance); CI runs it in the Safety policy job. `capabilities` lists what a
revision has.

Recorded evidence: an entry with `net.recorded` and no `command` holds the result of
a run made outside this runner (for example on a replay of a fork's history with the
nets overlaid on every commit). `results.recorded` is then true and names its
`source` and `verifier`; `run` reports such an entry as not-attempted without
checking anything out, so CI never re-runs it and never counts it as passing. The
optional `defects` table numbers the ledger rows (known-bad, fix, kind) and the
optional `recordedOverlays` table describes the trees recorded entries ran on.

Subcommands: validate, run, capabilities. See docs/rules/safety-evidence.md#net-proofs.
"""
import argparse
import json
import os
import re
import shutil
import signal
import subprocess
import sys
import tempfile
import time
from datetime import datetime, timezone
from pathlib import Path

import proof_provenance as provenance
import safety_common as common

LEDGER = f"{common.SAFETY_DIR}/net-proofs.json"
ADAPTERS_DIR = "tools/net-proofs/adapters/"
ADAPTER_MANIFEST = "adapter.json"
SIDES = ("knownBad", "fix")
# proven: fired (as expected) at known-bad AND quiet at fix. Every other state is not a pass.
STATES = ("proven", "not-fired", "fired-at-fix", "fired-differently", "not-reproduced",
          "not-applicable", "harness-error", "not-attempted")
FAILING = ("not-fired", "fired-at-fix", "fired-differently", "not-reproduced", "not-applicable", "harness-error")
# generic: a net not written for the defect fired as designed; reproduction: the fix's own test or repro
# turned into an attributable yell; model: an abstract model of the code (not the library) fired;
# tuned-after-fix: the net, its scenario or its rule was changed after reading the fix (or its subject).
LEVELS = ("generic", "reproduction", "model", "tuned-after-fix", "harness-smoke")
DEFECT_KINDS = ("plan", "upstream", "later-fix", "merge")
CLASSES = (1, 2, 3)
DEFAULT_RUNS = {1: 1, 2: 3, 3: 5}
MIN_RUNS = {1: 1, 2: 2, 3: 3}
ID = re.compile(r"[a-z0-9][a-z0-9-]*\Z")
INDEPENDENCE = re.compile(r"(designed-from-invariant|harness-smoke)(: .+)?\Z|tuned-after-fix: .{10,}\Z", re.S)
SKIPPED_DIRS = {".git", "bin", "obj", "node_modules"}
# {tree} {artifacts} {harness} {side} {run} {knownBadTree} {fixTree}; any other braces stay as written.
PLACEHOLDER = re.compile(r"\{(\w+)\}")


# --- ledger ------------------------------------------------------------------

def load(tree, report, tables=None):
    """(capabilities, proofs); `tables`, when given, receives the optional defects and recordedOverlays."""
    try:
        data = tree.read_json(LEDGER, {}) or {}
    except common.MalformedJson as error:
        report.error(str(error), LEDGER)
        return {}, []
    if data and data.get("schemaVersion") != 1:
        report.error(f"{LEDGER}: schemaVersion must be 1", LEDGER)
    capabilities = common.section(data, "capabilities", dict, report, LEDGER)
    if tables is not None:
        for name in ("defects", "recordedOverlays"):  # optional: absent means "not checked"
            tables[name] = common.section(data, name, dict, report, LEDGER) if name in data else None
    return capabilities, common.section(data, "proofs", list, report, LEDGER)


def validate_tables(tree, tables, capabilities, report):
    """The defects table (ledger rows) and the descriptions of trees that recorded entries ran on."""
    for row, defect in (tables.get("defects") or {}).items():
        label = f"Defect row {row}"
        if not str(row).isdigit() or int(row) < 1 or not isinstance(defect, dict):
            report.error(f"{label}: rows are positive integers mapped to an object", LEDGER)
            continue
        if not str(defect.get("defect", "")).strip() or defect.get("kind") not in DEFECT_KINDS:
            report.error(f"{label}: needs a defect description and a kind ({', '.join(DEFECT_KINDS)})", LEDGER)
        provenance.check_shape(defect.get("knownBad"), label, report, LEDGER, kinds=provenance.COMMIT_KINDS)
        commits = [(defect.get("fix") or {}).get("commit")] + list(defect.get("alsoKnownBad") or [])
        if not all(provenance.SHA.match(str(commit)) for commit in commits):
            report.error(f"{label}: fix.commit and alsoKnownBad are full 40-character commit ids", LEDGER)
    for name, overlay in (tables.get("recordedOverlays") or {}).items():
        label = f"Recorded overlay {name}"
        if not ID.match(name) or not isinstance(overlay, dict) or not str(overlay.get("description", "")).strip():
            report.error(f"{label}: needs a kebab-case name and a description", LEDGER)
            continue
        if "commit" in overlay and not provenance.SHA.match(str(overlay["commit"])):
            report.error(f"{label}: commit must be a full 40-character commit id", LEDGER)
        for adapter in _list(overlay, "adapters", label, report):
            _validate_adapter(tree, str(adapter), capabilities, label, report)


def validate_capabilities(capabilities, report):
    for name, spec in capabilities.items():
        label = f"Capability {name}"
        if not ID.match(name) or not isinstance(spec, dict) or not str(spec.get("description", "")).strip():
            report.error(f"{label}: needs a kebab-case name and a description", LEDGER)
            continue
        if ("file" in spec) == ("grep" in spec):
            report.error(f"{label}: declare exactly one probe, file or grep (with in)", LEDGER)
        elif "grep" in spec:
            if not isinstance(spec.get("in"), str):
                report.error(f"{label}: a grep probe names the files to search as 'in' (a glob)", LEDGER)
            try:
                re.compile(str(spec["grep"]), re.M)
            except re.error as error:
                report.error(f"{label}: grep is not a valid regular expression: {error}", LEDGER)


def validate_entry(tree, entry, capabilities, report, tables=None):
    """Offline checks of one net proof (and, with `tables`, its row and recorded overlay)."""
    label = f"Net proof {entry.get('id') or '<missing id>'}"
    if not ID.match(str(entry.get("id", ""))):
        report.error(f"{label}: id must be kebab-case", LEDGER)
    level = entry.get("level")
    if level not in LEVELS:
        report.error(f"{label}: level must be one of {', '.join(LEVELS)}", LEDGER)
    rows = entry.get("rows")
    if not isinstance(rows, list) or not all(isinstance(row, int) and not isinstance(row, bool) for row in rows):
        report.error(f"{label}: rows must be a list of ledger row numbers", LEDGER)
    elif (level == "harness-smoke") != (not rows):
        report.error(f"{label}: a harness-smoke entry proves no ledger row, and every other entry names its rows",
                     LEDGER)
    if not str(entry.get("defect", "")).strip():
        report.error(f"{label}: describe the defect", LEDGER)
    independence = str(entry.get("independence", ""))
    if not INDEPENDENCE.match(independence):
        report.error(f"{label}: independence must be designed-from-invariant, harness-smoke or "
                     "'tuned-after-fix: <what was tuned after reading the fix>'", LEDGER)
    elif level in ("generic", "model") and not independence.startswith("designed-from-invariant"):
        report.error(f"{label}: a {level} entry is designed from the invariant; a net changed after reading "
                     "the fix is level tuned-after-fix", LEDGER)
    elif level == "tuned-after-fix" and not independence.startswith("tuned-after-fix"):
        report.error(f"{label}: level tuned-after-fix says what was tuned: independence 'tuned-after-fix: ...'",
                     LEDGER)
    provenance.check_shape(entry.get("knownBad"), label, report, LEDGER, kinds=provenance.COMMIT_KINDS)
    fix = entry.get("fix") if isinstance(entry.get("fix"), dict) else {}
    if not provenance.SHA.match(str(fix.get("commit", ""))):
        report.error(f"{label}: fix.commit must be a full 40-character commit id", LEDGER)
    net = entry.get("net")
    if not isinstance(net, dict):
        report.error(f"{label}: net must be an object", LEDGER)
        return
    _validate_net(tree, net, capabilities, label, report)
    _validate_results(entry.get("results"), label, report)
    if tables is not None:
        _validate_row(entry, tables.get("defects"), label, report)
    _validate_recorded(tree, entry, (tables or {}).get("recordedOverlays"), capabilities, label, report)


def _validate_row(entry, defects, label, report):
    """Each row is in the defects table, and the entry's commits are that row's known-bad and fix."""
    if defects is None:
        return
    for row in entry.get("rows") or []:
        defect = defects.get(str(row))
        if not isinstance(defect, dict):
            report.error(f"{label}: row {row} is not in the defects table", LEDGER)
            continue
        bad = str((entry.get("knownBad") or {}).get("commit"))
        allowed = [str((defect.get("knownBad") or {}).get("commit"))]
        allowed += [str(commit) for commit in defect.get("alsoKnownBad") or []]
        fix = str((entry.get("fix") or {}).get("commit"))
        if bad not in allowed or fix != str((defect.get("fix") or {}).get("commit")):
            report.error(f"{label}: knownBad/fix differ from row {row} of the defects table", LEDGER)


def _validate_recorded(tree, entry, overlays, capabilities, label, report):
    """A recorded entry names the tree it ran on and carries a recorded result."""
    net = entry.get("net") if isinstance(entry.get("net"), dict) else {}
    recorded = net.get("recorded")
    results = entry.get("results") if isinstance(entry.get("results"), dict) else {}
    if recorded is None:
        if results.get("recorded"):
            report.error(f"{label}: a recorded result needs net.recorded (the tree it ran on)", LEDGER)
        return
    if not isinstance(recorded, dict) or not str(recorded.get("command", "")).strip():
        report.error(f"{label}: net.recorded is an object with the command that was run", LEDGER)
        return
    if overlays is not None and recorded.get("overlay") not in overlays:
        report.error(f"{label}: net.recorded.overlay names an entry of recordedOverlays", LEDGER)
    for side in ("knownBadTree", "fixTree"):
        if recorded.get(side) is not None and not provenance.SHA.match(str(recorded[side])):
            report.error(f"{label}: net.recorded.{side} must be a full 40-character commit id", LEDGER)
    for adapter in _list(recorded, "adapters", label, report):
        _validate_adapter(tree, str(adapter), capabilities, label, report)
    if net.get("command") is None:
        if not results.get("recorded") or not str(results.get("source", "")).strip() \
                or not str(results.get("verifier", "")).strip():
            report.error(f"{label}: a recorded entry has results with recorded: true, its source and verifier",
                         LEDGER)
            return
        if results.get("state") in ("not-applicable", "not-attempted", None):
            report.error(f"{label}: a recorded entry records what the net did, not {results.get('state')}", LEDGER)
        for side in SIDES:
            fired = (results.get(side) or {}).get("fired")
            if not isinstance(fired, bool) and not (side == "fix" and fired is None):
                report.error(f"{label}: results.{side}.fired is true or false (fix: null when not run)", LEDGER)


def _validate_net(tree, net, capabilities, label, report):
    if not str(net.get("name", "")).strip():
        report.error(f"{label}: net.name names the general net", LEDGER)
    evidence = net.get("evidenceClass")
    if evidence not in CLASSES:
        report.error(f"{label}: net.evidenceClass must be 1 (controlled), 2 (native stress) or 3 (performance)", LEDGER)
    requires = net.get("requires")
    if not isinstance(requires, list) or not requires:
        report.error(f"{label}: net.requires lists the capabilities the net needs (at least one)", LEDGER)
        requires = []
    for name in requires:
        if name not in capabilities:
            report.error(f"{label}: net.requires names {name!r}, which is not in the capability table", LEDGER)
    overlay = net.get("overlay", {})
    if not isinstance(overlay, dict):
        report.error(f"{label}: net.overlay must be an object", LEDGER)
        overlay = {}
    for commit in _list(overlay, "commits", label, report):
        if not provenance.SHA.match(str(commit)):
            report.error(f"{label}: overlay commit {commit!r} must be a full 40-character commit id", LEDGER)
    for patch in _list(overlay, "patches", label, report):
        if tree.read(str(patch)) is None:
            report.error(f"{label}: overlay patch {patch} does not exist", LEDGER)
    for adapter in _list(overlay, "adapters", label, report):
        _validate_adapter(tree, str(adapter), capabilities, label, report)
    command = net.get("command")
    if command is None:
        return  # a skeleton: capabilities are declared, the net is not runnable yet
    if not isinstance(command, list) or not command or not all(isinstance(part, str) for part in command):
        report.error(f"{label}: net.command must be a non-empty argument list", LEDGER)
    if not _positive(net.get("timeoutSeconds")):
        report.error(f"{label}: a runnable net declares its hard wall-clock limit as timeoutSeconds", LEDGER)
    build = net.get("build", ["LiteDB.sln"])
    if not isinstance(build, list) or not all(isinstance(item, str) for item in build):
        report.error(f"{label}: net.build must be a list of projects or solutions ([] builds nothing)", LEDGER)
    for key, valid in (("reports", _strings), ("buildArgs", _strings), ("replays", _positive),
                       ("buildTimeoutSeconds", _positive)):
        if key in net and not valid(net[key]):
            kind = "a list of strings" if valid is _strings else "a positive integer"
            report.error(f"{label}: net.{key} must be {kind}", LEDGER)
    runs = net.get("runs", DEFAULT_RUNS.get(evidence, 1))
    if evidence in CLASSES and (not isinstance(runs, int) or runs < MIN_RUNS[evidence]):
        report.error(f"{label}: evidence class {evidence} needs runs >= {MIN_RUNS[evidence]}", LEDGER)
    if evidence == 3:
        tolerance = net.get("tolerance")
        fraction = tolerance.get("fireFraction") if isinstance(tolerance, dict) else None
        if not isinstance(fraction, (int, float)) or not 0 < fraction <= 1:
            report.error(f"{label}: a performance net states its tolerance (tolerance.fireFraction in (0, 1])", LEDGER)
    expect = net.get("expect") if isinstance(net.get("expect"), dict) else {}
    bad, good = expect.get("knownBad") or {}, expect.get("fix") or {}
    if bad.get("fires") is not True or good.get("fires") is not False:
        report.error(f"{label}: expect.knownBad.fires must be true and expect.fix.fires false", LEDGER)
    try:
        re.compile(str(bad.get("match", "")))
        if not str(bad.get("match", "")).strip():
            report.error(f"{label}: expect.knownBad.match names the failure id or report text the net must yell",
                         LEDGER)
    except re.error as error:
        report.error(f"{label}: expect.knownBad.match is not a valid regular expression: {error}", LEDGER)


def _strings(value):
    return isinstance(value, list) and all(isinstance(item, str) for item in value)


def _positive(value):
    return isinstance(value, int) and not isinstance(value, bool) and value > 0


def _list(owner, key, label, report):
    value = owner.get(key, [])
    if not isinstance(value, list):
        report.error(f"{label}: overlay.{key} must be a list", LEDGER)
        return []
    return value


def _validate_adapter(tree, path, capabilities, label, report):
    folder = path.rstrip("/")
    if not folder.startswith(ADAPTERS_DIR) or "/" in folder[len(ADAPTERS_DIR):]:
        report.error(f"{label}: adapter {path} must be a directory directly under {ADAPTERS_DIR}", LEDGER)
        return
    try:
        manifest = tree.read_json(f"{folder}/{ADAPTER_MANIFEST}")
    except common.MalformedJson as error:
        report.error(f"{label}: {error}", LEDGER)
        return
    if not isinstance(manifest, dict):
        report.error(f"{label}: adapter {folder} has no {ADAPTER_MANIFEST}", LEDGER)
        return
    if not _safe_relative(manifest.get("target")):
        report.error(f"{label}: adapter {folder} needs a relative target directory inside the tree", LEDGER)
    for name in manifest.get("requires", []) if isinstance(manifest.get("requires", []), list) else [None]:
        if name not in capabilities:
            report.error(f"{label}: adapter {folder} requires {name!r}, which is not in the capability table", LEDGER)


def _safe_relative(path):
    return isinstance(path, str) and path and not path.startswith(("/", "\\")) and ".." not in Path(path).parts \
        and ":" not in path


def _validate_results(results, label, report):
    """A recorded result is consistent; a not-applicable result never reads as a pass."""
    if results is None:
        return
    if not isinstance(results, dict) or results.get("state") not in STATES:
        report.error(f"{label}: results.state must be one of {', '.join(STATES)}", LEDGER)
        return
    state = results["state"]
    if results.get("passed") is not None and results.get("passed") != (state == "proven"):
        report.error(f"{label}: results.passed contradicts state {state}; only proven passes", LEDGER)
    if state == "not-applicable" and not results.get("missing"):
        report.error(f"{label}: a not-applicable result lists the missing capabilities", LEDGER)
    bad, fix = results.get("knownBad") or {}, results.get("fix") or {}
    if state == "proven":
        if not bad.get("fired") or fix.get("fired") or results.get("missing"):
            report.error(f"{label}: a proven result fired at known-bad, stayed quiet at fix and missed no capability",
                         LEDGER)
    elif state == "not-fired" and bad.get("fired"):
        report.error(f"{label}: a not-fired result did not fire at known-bad", LEDGER)
    elif state == "fired-differently" and bad.get("fired") is False:
        report.error(f"{label}: a fired-differently result fired at known-bad", LEDGER)


def check_provenance(entry, report, dev_ref, offline=False):
    label = f"Net proof {entry.get('id')}"
    bad = entry.get("knownBad") or {}
    provenance.check_commit(bad, label, report, dev_ref, offline)
    fix = str((entry.get("fix") or {}).get("commit"))
    if not provenance.has_commit(fix):
        report.error(f"{label}: fix commit {fix} is not available in this clone")
    elif provenance.has_commit(str(bad.get("commit"))) and \
            provenance.run(["git", "merge-base", "--is-ancestor", str(bad.get("commit")), fix]) != 0:
        report.error(f"{label}: the fix {fix} does not descend from the known-bad commit {bad.get('commit')}")
    for commit in ((entry.get("net") or {}).get("overlay") or {}).get("commits", []):
        if not provenance.has_commit(str(commit)):
            report.error(f"{label}: overlay commit {commit} is not available in this clone")


def summarize(states):
    """Counts per state. Only proven passes; not-applicable and not-attempted are listed, never counted."""
    counts = {state: sum(1 for value in states if value == state) for state in STATES}
    return {"counts": counts, "passed": counts["proven"], "total": len(states),
            "notCounted": {state: counts[state] for state in ("not-applicable", "not-attempted")}}


def summary_line(summary):
    counts = summary["counts"]
    others = ", ".join(f"{count} {state}" for state, count in counts.items()
                       if count and state not in ("proven", "not-applicable", "not-attempted"))
    return (f"{summary['passed']} of {summary['total']} proven; {counts['not-applicable']} not applicable and "
            f"{counts['not-attempted']} not attempted (never counted as passing)" + (f"; {others}" if others else ""))


# --- capabilities --------------------------------------------------------------

def tree_files(root):
    """Repository-relative paths of a checked-out tree, without build output."""
    files = []
    for directory, folders, names in os.walk(root):
        folders[:] = [name for name in folders if name not in SKIPPED_DIRS]
        relative = Path(directory).relative_to(root)
        files += [(relative / name).as_posix() for name in names]
    return sorted(files)


def probe(capabilities, paths, read):
    """Names of the capabilities whose probe holds for a tree given as (paths, read(path))."""
    present, available = [], set(paths)
    for name, spec in sorted(capabilities.items()):
        if "file" in spec:
            found = spec["file"] in available
        else:
            pattern, files = re.compile(spec["grep"], re.M), common.glob_regex(spec["in"])
            found = any(files.match(path) and pattern.search(read(path) or "") for path in paths)
        if found:
            present.append(name)
    return present


def probe_directory(capabilities, root):
    root = Path(root)
    return probe(capabilities, tree_files(root),
                 lambda path: (root / path).read_text(encoding="utf-8-sig", errors="replace"))


def probe_revision(capabilities, rev):
    tree = common.Tree(rev)
    try:
        return probe(capabilities, tree.paths(), tree.read)
    finally:
        tree.close()


# --- run -----------------------------------------------------------------------

class HarnessError(Exception):
    """The runner could not observe the net (checkout, overlay, build, timeout): never a firing."""


class _Recorded(Exception):
    """A recorded-only entry: nothing is checked out or run, and it is never counted as passing."""


def _git(args, cwd):
    result = subprocess.run(["git", *args], cwd=cwd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, check=False)
    if result.returncode != 0:
        raise HarnessError(f"git {' '.join(args)} failed: {result.stdout.decode(errors='replace').strip()[-400:]}")
    return result.stdout.decode(errors="replace").strip()


def _resolve(bad):
    if not provenance.ensure_commit(bad):
        raise HarnessError(f"commit {bad.get('commit')} is not available (fetching its PR did not bring it)")
    return str(bad.get("commit"))


def apply_overlay(net, tree, harness):
    overlay = net.get("overlay") or {}
    if overlay.get("commits"):
        _git(["cherry-pick", "--no-commit", *overlay["commits"]], tree)
    for patch in overlay.get("patches", []):
        _git(["apply", "--whitespace=nowarn", str(Path(harness) / patch)], tree)
    for adapter in overlay.get("adapters", []):
        source = Path(harness) / adapter
        manifest = json.loads((source / ADAPTER_MANIFEST).read_text(encoding="utf-8"))
        target = Path(tree) / manifest["target"]
        for path in sorted(source.rglob("*")):
            if path.is_file() and path.relative_to(source).as_posix() not in (ADAPTER_MANIFEST, "README.md"):
                destination = target / path.relative_to(source)
                destination.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(path, destination)


def adapter_requirements(net, harness):
    names = []
    for adapter in (net.get("overlay") or {}).get("adapters", []):
        manifest = json.loads((Path(harness) / adapter / ADAPTER_MANIFEST).read_text(encoding="utf-8"))
        names += [name for name in manifest.get("requires", []) if name not in names]
    return names


def execute(command, cwd, log, timeout, env=None):
    """Run with a hard wall-clock limit (the whole process group is killed). Returns (exit code, seconds)."""
    started = time.monotonic()
    with open(log, "wb") as output:
        process = subprocess.Popen(command, cwd=cwd, stdout=output, stderr=subprocess.STDOUT,
                                   env={**os.environ, **(env or {})}, start_new_session=os.name == "posix")
        try:
            code = process.wait(timeout=timeout)
        except subprocess.TimeoutExpired:
            if os.name == "posix":
                os.killpg(process.pid, signal.SIGKILL)
            else:
                process.kill()
            process.wait()
            raise HarnessError(f"{command[0]} exceeded the {timeout} s wall-clock limit (log: {log})") from None
    return code, round(time.monotonic() - started, 2)


def _assertion(text, pattern):
    match = re.search(pattern, text, re.M)
    if match is None:
        return None
    return match.group("id") if "id" in match.re.groupindex else match.group(0)


class Run:
    """One net proof run: worktrees, overlay, capabilities, build, command, classification."""

    def __init__(self, entry, capabilities, work, harness, commits=None, keep=False):
        self.entry, self.net, self.capabilities = entry, entry.get("net") or {}, capabilities
        self.harness, self.keep, self.commits = Path(harness), keep, commits or {}
        stamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S%fZ")
        self.root = Path(work).resolve() / entry["id"] / stamp
        self.trees = {}
        self.result = {"schemaVersion": 1, "id": entry["id"], "rows": entry.get("rows"),
                       "net": self.net.get("name"), "evidenceClass": self.net.get("evidenceClass"),
                       "level": entry.get("level"), "independence": entry.get("independence"),
                       "harnessRevision": _harness_revision(harness), "startedAt": _now(),
                       "artifacts": str(self.root), "wallSeconds": {}, "knownBad": {}, "fix": {}}
        if self.commits:
            self.result["overridden"] = {side: commit for side, commit in self.commits.items()}
            self.result["note"] = "commits overridden on the command line: not evidence for the ledger entry"

    def side_dir(self, side):
        return self.root / side

    def execute(self):
        started = time.monotonic()
        try:
            if self.net.get("recorded") is not None and self.net.get("command") is None:
                recorded = self.net["recorded"]
                state = (self.entry.get("results") or {}).get("state")
                raise _Recorded(f"recorded evidence on {recorded.get('overlay') or 'a recorded tree'} "
                                f"(recorded state: {state}); this runner does not rebuild that tree")
            self._prepare()
            missing = {side: self.result[side]["missing"] for side in SIDES if self.result[side]["missing"]}
            if missing:
                self._finish("not-applicable", "required capabilities are missing: " + "; ".join(
                    f"{side}: {', '.join(names)}" for side, names in missing.items()))
                self.result["missing"] = sorted({name for names in missing.values() for name in names})
            elif self.net.get("command") is None:
                self._finish("not-attempted", "skeleton entry: the net has no command yet")
            else:
                self._build()
                self._run_net()
        except _Recorded as recorded:
            self._finish("not-attempted", str(recorded))
        except HarnessError as error:
            self._finish("harness-error", str(error))
        except Exception as error:  # a runner defect is a harness error with its type, never a net result
            self._finish("harness-error", f"{type(error).__name__}: {error}")
        finally:
            self.result["wallSeconds"]["total"] = round(time.monotonic() - started, 2)
            self._cleanup()
        self.result["passed"] = self.result["state"] == "proven"
        self.root.mkdir(parents=True, exist_ok=True)
        (self.root / "result.json").write_text(json.dumps(self.result, indent=2) + "\n", encoding="utf-8")
        return self.result

    def _finish(self, state, reason):
        self.result["state"], self.result["reason"] = state, reason

    def _prepare(self):
        started = time.monotonic()
        bad = dict(self.entry.get("knownBad") or {})
        commits = {"knownBad": self.commits.get("knownBad") or _resolve(bad),
                   "fix": self.commits.get("fix") or str((self.entry.get("fix") or {}).get("commit"))}
        if not self.commits.get("fix") and bad.get("kind") == "pr-commit":
            provenance.ensure_commit({**bad, "commit": commits["fix"]})
        adapter_needs = adapter_requirements(self.net, self.harness)
        for side in SIDES:
            commit = _git(["rev-parse", "--verify", f"{commits[side]}^{{commit}}"], common.repo_root())
            tree = self.side_dir(side) / "tree"
            (self.side_dir(side) / "artifacts").mkdir(parents=True, exist_ok=True)
            _git(["worktree", "add", "--detach", str(tree), commit], common.repo_root())
            self.trees[side] = tree
            plain = probe_directory(self.capabilities, tree)
            record = self.result[side]
            record.update({"commit": commit, "capabilities": {"plain": plain}})
            record["missing"] = [name for name in adapter_needs if name not in plain]
            if record["missing"]:
                continue  # an adapter that needs what the revision lacks is never applied
            apply_overlay(self.net, tree, self.harness)
            overlaid = probe_directory(self.capabilities, tree)
            record["capabilities"]["overlay"] = overlaid
            record["missing"] = [name for name in self.net.get("requires", []) if name not in overlaid]
        self.result["wallSeconds"]["prepare"] = round(time.monotonic() - started, 2)

    def _build(self):
        started = time.monotonic()
        limit = self.net.get("buildTimeoutSeconds", 1800)
        for side in SIDES:
            for index, target in enumerate(self.net.get("build", ["LiteDB.sln"])):
                log = self.side_dir(side) / "artifacts" / f"build-{index}.log"
                code, _ = execute(["dotnet", "build", target, "-c", "Release", "-p:TestingEnabled=true",
                                   *self.net.get("buildArgs", [])], self.trees[side], log, limit)
                if code != 0:
                    raise HarnessError(f"build of {target} failed at {side} (exit {code}; log: {log})")
        self.result["wallSeconds"]["build"] = round(time.monotonic() - started, 2)

    def _run_net(self):
        started = time.monotonic()
        evidence = self.net["evidenceClass"]
        runs = self.net.get("runs", DEFAULT_RUNS[evidence])
        if evidence == 3:  # paired: alternate the sides so drift affects both alike
            for index in range(runs):
                for side in SIDES:
                    self._attempt(side, index)
        else:
            for side in SIDES:
                for index in range(runs):
                    self._attempt(side, index)
            if evidence == 1:  # a controlled net must replay: the same assertion again
                for index in range(runs, runs + self.net.get("replays", 1)):
                    if self.result["knownBad"]["attempts"][0]["assertion"] is None:
                        break
                    self._attempt("knownBad", index, replay=True)
        self.result["wallSeconds"]["net"] = round(time.monotonic() - started, 2)
        self._classify(evidence)

    def _attempt(self, side, index, replay=False):
        artifacts = self.side_dir(side) / "artifacts" / f"run-{index}"
        artifacts.mkdir(parents=True, exist_ok=True)
        values = {"tree": str(self.trees[side]), "artifacts": str(artifacts), "harness": str(self.harness),
                  "side": side, "run": str(index), "knownBadTree": str(self.trees["knownBad"]),
                  "fixTree": str(self.trees["fix"])}
        command = [PLACEHOLDER.sub(lambda match: values.get(match.group(1), match.group(0)), part)
                   for part in self.net["command"]]
        log = artifacts / "output.log"
        code, seconds = execute(command, self.trees[side], log, self.net["timeoutSeconds"],
                                {"NET_PROOF_SIDE": side, "NET_PROOF_ARTIFACTS": str(artifacts)})
        text = log.read_text(encoding="utf-8", errors="replace")
        for pattern in self.net.get("reports", []):
            for path in sorted(artifacts.glob(pattern)):
                text += "\n" + path.read_text(encoding="utf-8", errors="replace")
        expect = (self.net.get("expect") or {}).get("knownBad") or {}
        attempt = {"run": index, "replay": replay, "exitCode": code, "fired": code != 0, "wallSeconds": seconds,
                   "assertion": _assertion(text, expect.get("match", "(?!)")) if code != 0 else None,
                   "log": str(log), "files": sorted(path.relative_to(artifacts).as_posix()
                                                    for path in artifacts.rglob("*") if path.is_file())}
        metric = (self.net.get("tolerance") or {}).get("metric")
        if metric:
            found = re.search(metric, text, re.M)
            attempt["metric"] = float(found.group("value")) if found else None
        self.result[side].setdefault("attempts", []).append(attempt)

    def _classify(self, evidence):
        fraction = (self.net.get("tolerance") or {}).get("fireFraction", 0)
        for side in SIDES:
            record = self.result[side]
            ordinary = [attempt for attempt in record["attempts"] if not attempt["replay"]]
            rate = sum(attempt["fired"] for attempt in ordinary) / len(ordinary)
            record["fireRate"] = round(rate, 3)
            record["fired"] = rate >= fraction if evidence == 3 else rate > 0
            metrics = sorted(attempt["metric"] for attempt in ordinary if attempt.get("metric") is not None)
            if metrics:
                record["metricMedian"] = metrics[len(metrics) // 2]
        bad, fix = self.result["knownBad"], self.result["fix"]
        # None: the attempt did not fire, or fired without the expected assertion.
        assertions = [attempt["assertion"] if attempt["fired"] else None for attempt in bad["attempts"]]
        matched = [value for value in assertions if value is not None]
        bad["assertion"] = matched[0] if matched else None
        if not bad["fired"]:
            self._finish("not-fired", "the net stayed quiet at the known-bad commit")
        elif not matched:
            self._finish("fired-differently", "the net failed at known-bad, but not with the expected assertion")
        elif evidence == 1 and len(set(assertions)) != 1:
            self._finish("not-reproduced", "a controlled net must reproduce the same assertion on replay; "
                                           f"observed {assertions}")
        elif fix["fired"]:
            self._finish("fired-at-fix", "the net also fired at the fix (kept as a finding; never a pass)")
        else:
            self._finish("proven", f"fired at known-bad ({bad['assertion']}) and stayed quiet at the fix")

    def _cleanup(self):
        if self.keep:
            return
        for tree in self.trees.values():
            subprocess.run(["git", "worktree", "remove", "--force", str(tree)], cwd=common.repo_root(),
                           stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=False)
        subprocess.run(["git", "worktree", "prune"], cwd=common.repo_root(), check=False)


def _now():
    return datetime.now(timezone.utc).isoformat(timespec="seconds")


def _harness_revision(harness):
    head = subprocess.run(["git", "rev-parse", "HEAD"], cwd=harness, stdout=subprocess.PIPE,
                          stderr=subprocess.DEVNULL, check=False).stdout.decode().strip()
    dirty = subprocess.run(["git", "status", "--porcelain", "--untracked-files=no"], cwd=harness,
                           stdout=subprocess.PIPE, check=False).stdout.strip()
    return head + ("+dirty" if dirty else "")


# --- command line --------------------------------------------------------------

def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    commands = parser.add_subparsers(dest="command", required=True)
    check = commands.add_parser("validate", help="Validate the ledger (offline unless --provenance)")
    check.add_argument("--head", default="HEAD", help="Revision to validate (WORKTREE for local files)")
    check.add_argument("--provenance", action="store_true", help="Also verify the commits (git, network)")
    check.add_argument("--dev-ref", default="origin/dev")
    run = commands.add_parser("run", help="Run net proofs and write result JSON")
    chosen = run.add_mutually_exclusive_group(required=True)
    chosen.add_argument("--id", help="The proof to run")
    chosen.add_argument("--all", action="store_true",
                        help="Every proof (skeletons and recorded-only entries report not-attempted)")
    run.add_argument("--work-dir", default=str(Path(tempfile.gettempdir()) / "litedb-net-proofs"),
                     help="Worktrees and artifacts go below this directory")
    run.add_argument("--keep", action="store_true", help="Keep the worktrees after the run")
    run.add_argument("--known-bad", dest="known_bad", help="Run against this revision instead (not ledger evidence)")
    run.add_argument("--fix", help="Use this revision as the fix instead (not ledger evidence)")
    run.add_argument("--head", default=common.WORKTREE, help="Revision to read the ledger from")
    caps = commands.add_parser("capabilities", help="List the capabilities a revision has (no overlay)")
    caps.add_argument("--rev", default="HEAD")
    caps.add_argument("--head", default=common.WORKTREE, help="Revision to read the capability table from")
    args = parser.parse_args(argv)

    report = common.Report("Net proof")
    head = common.Tree(args.head)
    tables = {}
    capabilities, entries = load(head, report, tables)
    if args.command == "capabilities":
        commit = common.git("rev-parse", "--verify", f"{args.rev}^{{commit}}").strip()
        print(json.dumps({"rev": commit, "capabilities": probe_revision(capabilities, commit)}, indent=2))
        return report.finish() if report.errors else 0
    validate_capabilities(capabilities, report)
    validate_tables(head, tables, capabilities, report)
    ids = [entry.get("id") for entry in entries]
    for duplicate in sorted({value for value in ids if ids.count(value) > 1}, key=str):
        report.error(f"{duplicate} has more than one net proof", LEDGER)
    for entry in entries:
        validate_entry(head, entry, capabilities, report, tables)
    if args.command == "validate":
        for entry in entries if args.provenance else []:
            check_provenance(entry, report, args.dev_ref)
        recorded = [(entry.get("results") or {}).get("state") for entry in entries if entry.get("results")]
        report.section(f"{len(entries)} net proofs in `{LEDGER}`; recorded results: "
                       f"{summary_line(summarize(recorded)) if recorded else 'none'}.")
        return report.finish()
    if report.errors:
        return report.finish()
    selected = entries if args.all else [entry for entry in entries if entry.get("id") == args.id]
    if not selected:
        report.error(f"No net proof {args.id} in {LEDGER}", LEDGER)
        return report.finish()
    commits = {side: value for side, value in (("knownBad", args.known_bad), ("fix", args.fix)) if value}
    results = []
    for entry in selected:
        result = Run(entry, capabilities, args.work_dir, common.repo_root(), commits, args.keep).execute()
        results.append(result)
        line = f"`{result['id']}`: **{result['state']}** ({result['reason']}); {result['artifacts']}/result.json"
        (report.error if result["state"] in FAILING else report.section)(line)
    summary = summarize([result["state"] for result in results])
    report.section(summary_line(summary))
    Path(args.work_dir).mkdir(parents=True, exist_ok=True)
    (Path(args.work_dir) / "summary.json").write_text(json.dumps({**summary, "results": [
        {"id": result["id"], "state": result["state"], "result": f"{result['artifacts']}/result.json"}
        for result in results]}, indent=2) + "\n", encoding="utf-8")
    return report.finish()


if __name__ == "__main__":
    sys.exit(main())
