"""Fuzz coverage map: which LiteDB source files each fuzz target actually executes.

Walks one or more LiteDB.Fuzz artifact roots written with --coverage-guided. Every
run directory holds run.json (its target) and coverage.xml (dotnet-coverage's XML
format: <module name="LiteDB.dll"> with <function>/<range covered="yes|partial|no"
source_id=...> and a per-module <source_files> table of absolute paths). A source
file counts for a target when at least one of its ranges in LiteDB.dll was covered
in any run of that target. The runner replays the permanent corpus without
coverage collection, so only the generated seed runs contribute.

The output is the coverage stage of select_fuzz_targets.py:

  {"schemaVersion": 1,
   "generatedFrom": {"commit", "command", "date", "targets": [...], "notCovered": [...], "refresh"},
   "targets": {"<target>": ["LiteDB/Engine/Disk/DiskService.cs", ...]}}

Lists are sorted and the JSON is written deterministically, so regenerating from
the same runs gives the same bytes. Refresh the checked-in map after adding or
substantially changing a target (the map is only as current as its runs):

  dotnet run --project LiteDB.Fuzz -c Release -f net8.0 --no-build -- --target all \\
      --seed 2947 --count 3 --coverage-guided --artifact-dir artifacts_temp/coverage-map
  python .github/scripts/generate_fuzz_coverage_map.py artifacts_temp/coverage-map \\
      --command "<the command above>"

Run it against a fresh artifact root: a reused root also replays its retained
coverage corpus, which is harmless but no longer matches the recorded command.
"""
import argparse
import json
import os
import sys
import xml.etree.ElementTree as ElementTree
from pathlib import Path

import safety_common as common

DEFAULT_OUTPUT = f"{common.SAFETY_DIR}/fuzz-coverage-map.json"
OBLIGATIONS = f"{common.SAFETY_DIR}/fuzz-obligations.json"
MODULE = "LiteDB.dll"
PREFIX = "LiteDB/"
REFRESH = ("Rerun every target with --coverage-guided into a fresh artifact root and regenerate with "
           ".github/scripts/generate_fuzz_coverage_map.py (see its --help) after adding or changing a target.")


def find_runs(roots):
    """Yield (run directory, run.json data) for every run directory below the roots that has coverage.xml."""
    for root in roots:
        for directory, _, files in sorted(os.walk(root)):
            if "run.json" in files and "coverage.xml" in files:
                with open(os.path.join(directory, "run.json"), encoding="utf-8-sig") as handle:
                    yield Path(directory), json.load(handle)


def covered_sources(path, module=MODULE):
    """Absolute source paths of `module` with at least one covered (yes/partial) range."""
    covered, sources, in_module = set(), {}, False
    for event, element in ElementTree.iterparse(path, events=("start", "end")):
        if element.tag == "module":
            if event == "start":
                in_module = (element.get("name") or "").lower() == module.lower()
            else:
                in_module = False
                element.clear()
            continue
        if event != "end" or not in_module:
            continue
        if element.tag == "range":
            if element.get("covered", "no").lower() != "no":
                covered.add(element.get("source_id"))
        elif element.tag == "source_file":
            sources[element.get("id")] = element.get("path") or ""
        elif element.tag == "function":
            element.clear()
    return {sources[source_id] for source_id in covered if source_id in sources}


class PathResolver:
    """Map absolute paths recorded on any machine to repository-relative tracked paths."""

    def __init__(self, tracked):
        self.tracked = set(tracked)

    def resolve(self, absolute):
        parts = absolute.replace("\\", "/").split("/")
        for index in range(len(parts)):
            candidate = "/".join(parts[index:])
            if candidate in self.tracked:
                return candidate
        return None


def build_map(roots, tracked, prefix=PREFIX, module=MODULE):
    """Return ({target: sorted files}, [run.json data]) for the runs below the roots."""
    resolver = PathResolver(tracked)
    targets, runs = {}, []
    for directory, run in find_runs(roots):
        target = run.get("target")
        if not target:
            continue
        runs.append(run)
        files = targets.setdefault(target, set())
        for absolute in covered_sources(directory / "coverage.xml", module):
            relative = resolver.resolve(absolute)
            if relative and relative.startswith(prefix):
                files.add(relative)
    return {target: sorted(files) for target, files in sorted(targets.items())}, runs


def render(targets, runs, command, commit=None, date=None, expected=(), note=None):
    commits = sorted({run.get("gitSha") for run in runs if run.get("gitSha")})
    finished = sorted(run.get("finishedUtc") for run in runs if run.get("finishedUtc"))
    document = {
        "schemaVersion": 1,
        "description": "Per fuzz target, the LiteDB/ source files with at least one covered line in a "
                       "--coverage-guided run. The coverage stage of select_fuzz_targets.py; generated by "
                       "generate_fuzz_coverage_map.py, never edited by hand.",
        "generatedFrom": {
            "commit": commit or (commits[0] if len(commits) == 1 else ",".join(commits)),
            "command": command,
            "date": date or (finished[-1][:10] if finished else None),
            "runs": len(runs),
            "targets": sorted(targets),
            "notCovered": sorted(set(expected) - set(targets)),
            **({"note": note} if note else {}),
            "refresh": REFRESH,
        },
        "targets": targets,
    }
    return json.dumps(document, indent=1, sort_keys=False) + "\n"


def pending_targets(tree):
    """Targets the obligation map declares as pending (added by other changes, not yet runnable)."""
    try:
        policy = tree.read_json(OBLIGATIONS) or {}
    except common.MalformedJson:
        return []
    pending = policy.get("pendingTargets", []) if isinstance(policy, dict) else []
    return [name for name in pending if isinstance(name, str)]


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0], epilog=__doc__.split("\n\n", 1)[1],
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("roots", nargs="+", help="LiteDB.Fuzz artifact roots (searched recursively)")
    parser.add_argument("--output", default=None, help=f"Output JSON (default: {DEFAULT_OUTPUT} in the repository)")
    parser.add_argument("--command", required=True, help="The exact fuzz command that produced the runs")
    parser.add_argument("--commit", help="Source commit (default: the gitSha the runs recorded)")
    parser.add_argument("--date", help="Generation date (default: the last run's finish date)")
    parser.add_argument("--prefix", default=PREFIX, help="Keep only repository paths under this prefix")
    parser.add_argument("--note", help="Free text recorded in generatedFrom (wall time, host load, caveats)")
    args = parser.parse_args(argv)
    tree = common.Tree(common.WORKTREE)
    targets, runs = build_map(args.roots, tree.paths(), args.prefix)
    if not runs:
        print("ERROR: no run directory with run.json and coverage.xml below " + ", ".join(args.roots))
        return 1
    expected = set(common.fuzz_targets(tree)) | set(pending_targets(tree))
    output = args.output or os.path.join(common.repo_root(), DEFAULT_OUTPUT)
    Path(output).write_text(render(targets, runs, args.command, args.commit, args.date, expected, args.note),
                            encoding="utf-8", newline="\n")
    empty = sorted(target for target, files in targets.items() if not files)
    print(f"Wrote {output}: {len(targets)} targets from {len(runs)} runs; "
          f"{sum(len(files) for files in targets.values())} target/file pairs.")
    for target in empty:
        print(f"WARNING: target {target} covered no {args.prefix} file")
    missing = sorted(set(expected) - set(targets))
    if missing:
        print("WARNING: no coverage for targets " + ", ".join(missing) + " (recorded under generatedFrom.notCovered)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
