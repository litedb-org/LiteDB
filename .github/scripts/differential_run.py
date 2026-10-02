"""Differential run: the same fuzz seeds on the merge-base and the head, compared per operation.

Each tree is built in its own worktree (LiteDB.Fuzz, Release, TestingEnabled=true) and
runs ITS OWN harness on the same targets, seeds and counts. Every run directory's
`outcomes.jsonl`, `closed-clean.jsonl` and `markers.json` (docs/rules/safety-evidence.md,
outcome records) are aggregated per (op, dimension) and compared:

- escaped exceptions: the set of exception types (+ errorCode) per operation class;
- outcomes: the set of outcome kinds (ok/threw/refused/hang); a share shift is advisory;
- latency: p50/p99 of elapsedMs. Advisory beyond --latency-relative AND --latency-floor-ms;
  failing only beyond --latency-hard-factor AND the floor. Defaults 0.5 (50 %), 5 ms and
  3x: fuzz operations take well under a millisecond, shared CI runners vary by +-50 %
  between runs, so only a multiple above an absolute floor is a signal rather than noise
  (a fixed-interval poll replacing a wake-up is 10x or more). Judged only with at least
  --latency-min-samples samples per side, because a p99 of fewer samples is its maximum;
- ClosedClean metrics: a numeric metric whose maximum grew, or a boolean value never
  seen on the base, per (op, dimension);
- markers: a marker the base reached and the head never reaches fails; new hits are advisory.

A difference fails unless an entry ADDED to .github/safety/intended-changes.json by this
change covers it (check_intended_changes.py); an added entry whose change is not observed
fails as well, because the PR claims a contract change nobody exercised (latency claims
excepted: noise may hide them). Output: a markdown report (PR body / step summary) and JSON.

Both trees must contain the outcome-emitting harness (`outcomes.jsonl` written by
LiteDB.Fuzz); every PR based on a dev that includes it qualifies. When the base lacks it
the run is SKIPPED with a visible warning, never passed silently. To compare older trees,
create a worktree of the old commit, apply the harness commits on top (cherry-pick, as
the net proofs do) and pass it with --base-tree/--head-tree.
"""
import argparse
import json
import math
import os
import shutil
import subprocess
import sys
import tempfile
from collections import Counter, defaultdict
from pathlib import Path

import check_intended_changes as intended
import safety_common as common

HARNESS_FILE = "outcomes.jsonl"
FUZZ_PROJECT = "LiteDB.Fuzz/LiteDB.Fuzz.csproj"
IDENTITY = {"target", "step", "seed", "op", "dimension", "path", "file", "run", "worker", "elapsedMs", "timestamp"}
DEFAULT_TARGETS = "chaos,concurrent,cursor-handoff,conflict,integrity"


# --- running ---------------------------------------------------------------

def has_harness(tree_path):
    """True when the tree's fuzz harness sources (LiteDB.Fuzz and the probes it links) write outcomes.jsonl."""
    roots = [Path(tree_path) / "LiteDB.Fuzz", Path(tree_path) / "LiteDB.Tests" / "Safety"]
    return any(HARNESS_FILE in path.read_text(encoding="utf-8", errors="replace")
               for root in roots if root.is_dir() for path in root.rglob("*.cs")
               if not {"bin", "obj"} & set(path.relative_to(root).parts))


def pr_seeds(number, count):
    """Fixed seeds per PR number, so reruns of one PR compare the same schedules."""
    return [100_000 + (number * 7_919 + index * 104_729) % 900_000 for index in range(count)]


def checkout(rev, workdir, label):
    path = Path(workdir) / label
    common.git("worktree", "add", "--detach", "--force", str(path), rev)
    return path


def build(tree, framework, log):
    command = ["dotnet", "build", str(Path(tree) / FUZZ_PROJECT), "-c", "Release", "-f", framework,
               "-p:TestingEnabled=true", "-nologo", "-v", "q"]
    with open(log, "w", encoding="utf-8") as handle:
        return subprocess.run(command, stdout=handle, stderr=subprocess.STDOUT).returncode


def run_targets(tree, framework, targets, seeds, count, out, timeout):
    """Run every (target, seed) once; return [{target, seed, exitCode, directory}]."""
    dll = Path(tree) / "LiteDB.Fuzz" / "bin" / "Release" / framework / "LiteDB.Fuzz.dll"
    runs = []
    for target in targets:
        for seed in seeds:
            directory = Path(out) / f"{target}-s{seed}"
            command = ["dotnet", str(dll), "--target", target, "--seed", str(seed), "--count", str(count),
                       "--artifact-dir", str(directory)]
            try:
                result = subprocess.run(command, cwd=tree, capture_output=True, text=True, timeout=timeout)
                code, tail = result.returncode, (result.stdout + result.stderr)[-2000:]
            except subprocess.TimeoutExpired:
                code, tail = "timeout", f"exceeded {timeout} s"
            runs.append({"target": target, "seed": seed, "count": count, "exitCode": code,
                         "directory": str(directory), "outputTail": tail})
    return runs


# --- collecting ------------------------------------------------------------

def _jsonl(path):
    if not path.is_file():
        return []
    lines = (line.strip() for line in path.read_text(encoding="utf-8", errors="replace").splitlines())
    return [json.loads(line) for line in lines if line]


def collect(roots, requested=None):
    """Aggregate every run directory below roots; requested = {(target, seed)} filters corpus replays."""
    summary = {"ops": defaultdict(lambda: {"outcomes": Counter(), "exceptions": Counter(), "elapsed": []}),
               "closed": defaultdict(lambda: defaultdict(list)), "markers": Counter(), "runs": 0, "withOutcomes": 0}
    for root in roots:
        for run_json in sorted(Path(root).rglob("run.json")):
            directory = run_json.parent
            meta = json.loads(run_json.read_text(encoding="utf-8"))
            if requested is not None and (meta.get("target"), meta.get("seed")) not in requested:
                continue
            summary["runs"] += 1
            outcomes = _jsonl(directory / HARNESS_FILE)
            summary["withOutcomes"] += bool(outcomes) or (directory / HARNESS_FILE).is_file()
            for record in outcomes:
                entry = summary["ops"][(record.get("op"), record.get("dimension") or "")]
                entry["outcomes"][record.get("outcome")] += 1
                if record.get("exceptionType"):
                    code = record.get("errorCode")
                    entry["exceptions"][record["exceptionType"] + (f"#{code}" if code is not None else "")] += 1
                if isinstance(record.get("elapsedMs"), (int, float)):
                    entry["elapsed"].append(float(record["elapsedMs"]))
            for record in _jsonl(directory / "closed-clean.jsonl"):
                key = (record.get("op") or "ClosedClean", record.get("dimension") or "")
                for metric, value in record.items():
                    if metric not in IDENTITY and isinstance(value, (bool, int, float)):
                        summary["closed"][key][metric].append(value)
            markers = directory / "markers.json"
            if markers.is_file():
                summary["markers"].update(json.loads(markers.read_text(encoding="utf-8")).get("hits", {}))
    return summary


def percentile(values, fraction):
    ordered = sorted(values)
    return ordered[min(len(ordered) - 1, max(0, math.ceil(fraction * len(ordered)) - 1))] if ordered else None


# --- comparing -------------------------------------------------------------

def compare(base, head, settings):
    """Return the list of differences: {kind, op, dimension, detail, severity, ...}."""
    found = []

    def add(kind, op, dimension, detail, severity="fail", **extra):
        found.append({"kind": kind, "op": op, "dimension": dimension, "detail": detail, "severity": severity, **extra})

    for key in sorted(set(base["ops"]) | set(head["ops"]), key=str):
        op, dimension = key
        if key not in head["ops"]:
            add("outcome-change", op, dimension, "operation class no longer exercised on the head")
            continue
        if key not in base["ops"]:
            add("outcome-change", op, dimension, "new operation class (no baseline)", "advisory")
            continue
        old, new = base["ops"][key], head["ops"][key]
        for exception in sorted(set(new["exceptions"]) - set(old["exceptions"])):
            add("new-exception", op, dimension, f"escapes {exception} ({new['exceptions'][exception]}x)",
                exception=exception)
        for exception in sorted(set(old["exceptions"]) - set(new["exceptions"])):
            add("exception-removed", op, dimension, f"no longer escapes {exception}", exception=exception)
        kinds_old, kinds_new = set(old["outcomes"]), set(new["outcomes"])
        if kinds_old != kinds_new:
            add("outcome-change", op, dimension, f"outcomes {sorted(kinds_old)} -> {sorted(kinds_new)}",
                kinds=sorted(kinds_old ^ kinds_new))
        else:
            for kind in sorted(kinds_old):
                share_old = old["outcomes"][kind] / sum(old["outcomes"].values())
                share_new = new["outcomes"][kind] / sum(new["outcomes"].values())
                if abs(share_new - share_old) > settings.outcome_share:
                    add("outcome-change", op, dimension, f"'{kind}' share {share_old:.0%} -> {share_new:.0%}",
                        "advisory")
        latency(add, op, dimension, old["elapsed"], new["elapsed"], settings)
    for key in sorted(set(base["closed"]) | set(head["closed"]), key=str):
        old, new = base["closed"].get(key, {}), head["closed"].get(key, {})
        for metric in sorted(set(old) & set(new)):
            if any(isinstance(value, bool) for value in old[metric] + new[metric]):
                unseen = set(new[metric]) - set(old[metric])
                if unseen:
                    add("closed-clean", metric, key[1], f"{key[0]}: {metric} now {sorted(unseen)}")
            elif max(new[metric]) > max(old[metric]) + settings.closed_clean_tolerance:
                add("closed-clean", metric, key[1], f"{key[0]}: max {metric} {max(old[metric])} -> {max(new[metric])}")
    for marker in sorted(set(base["markers"]) | set(head["markers"])):
        if base["markers"][marker] and not head["markers"][marker]:
            add("marker", marker, "", f"reached {base['markers'][marker]}x on the base, never on the head")
        elif head["markers"][marker] and not base["markers"][marker]:
            add("marker", marker, "", f"newly reached ({head['markers'][marker]}x)", "advisory")
    return found


def latency(add, op, dimension, old, new, settings):
    if min(len(old), len(new)) < settings.latency_min_samples:
        return
    for name, fraction in (("p50", 0.5), ("p99", 0.99)):
        before, after = percentile(old, fraction), percentile(new, fraction)
        if after - before <= settings.latency_floor_ms:
            continue
        if after > before * settings.latency_hard_factor:
            add("latency", op, dimension, f"{name} {before:.2f} -> {after:.2f} ms (> {settings.latency_hard_factor}x)")
        elif after > before * (1 + settings.latency_relative):
            add("latency", op, dimension, f"{name} {before:.2f} -> {after:.2f} ms", "advisory")


def apply_manifest(differences, entries, base, head):
    """Mark differences an entry covers; return entries that cover nothing observed."""
    used = set()
    for difference in differences:
        for index, entry in enumerate(entries):
            named = difference.get("exception", "").split("#")[0]
            side = entry.get("after") if difference["kind"] == "new-exception" else entry.get("before")
            if intended.covers(entry, difference["kind"], difference["op"], difference["dimension"]) and (
                    not named or named in str(side) or named.split(".")[-1] in str(side)):
                difference["intended"] = entry.get("doc")
                used.add(index)
                break
    explained = {(d["op"], d["dimension"]): d["intended"] for d in differences
                 if d.get("intended") and d["kind"] in ("new-exception", "exception-removed")}
    for difference in differences:  # an intended exception change explains 'threw' appearing or vanishing
        if difference["kind"] == "outcome-change" and not difference.get("intended") \
                and set(difference.get("kinds", ["?"])) <= {"threw", "refused"}:
            difference["intended"] = explained.get((difference["op"], difference["dimension"]))
    unused = []
    for index, entry in enumerate(entries):
        if index in used:
            continue
        exercised = any(op == entry.get("call") for op, _ in set(base["ops"]) & set(head["ops"])) \
            or entry.get("call") in base["markers"] or entry.get("change") == "closed-clean"
        unused.append({**entry, "severity": "advisory" if entry.get("change") == "latency" else "fail",
                       "why": "claimed change not observed" if exercised else "claimed call not exercised"})
    return unused


# --- reporting -------------------------------------------------------------

def render(result):
    status = result["status"]
    lines = [f"## Differential run: {status}", ""]
    if status == "skipped":
        return "\n".join(lines + [f"> **Skipped:** {result['reason']}", ""])
    lines += [f"Base `{result['base']['rev']}` vs head `{result['head']['rev']}`; targets "
              f"{', '.join(result['settings']['targets'])}; seeds {result['settings']['seeds']}; "
              f"count {result['settings']['count']}. Runs with outcomes: base "
              f"{result['base']['withOutcomes']}/{result['base']['runs']}, head "
              f"{result['head']['withOutcomes']}/{result['head']['runs']}.", ""]
    rows = [d for d in result["differences"] if d["severity"] == "fail" or d.get("intended")]
    if rows:
        lines += ["| Change | Operation | Dimension | Observed | Intended by |", "| --- | --- | --- | --- | --- |"]
        lines += [f"| {d['kind']} | `{d['op']}` | {d['dimension'] or '-'} | {d['detail']} | "
                  f"{d.get('intended') or '**not in manifest**'} |" for d in rows]
    else:
        lines.append("No behavior difference that needs a manifest entry.")
    for entry in result["unusedEntries"]:
        lines.append(f"- {'**' + entry['why'] + '**' if entry['severity'] == 'fail' else entry['why']}: "
                     f"`{entry.get('call')}` {entry.get('change')} ({entry.get('doc')})")
    advisories = [d for d in result["differences"] if d["severity"] == "advisory"]
    if advisories:
        lines += ["", "<details><summary>Advisory differences</summary>", ""]
        lines += [f"- {d['kind']} `{d['op']}` {d['dimension']}: {d['detail']}" for d in advisories]
        lines += ["", "</details>"]
    for side in ("base", "head"):
        failed = [run for run in result[side].get("fuzzRuns", []) if run["exitCode"] != 0]
        if failed:
            lines.append(f"- {side}: {len(failed)} fuzz run(s) failed; their outcomes are truncated: "
                         + ", ".join(f"{run['target']}/{run['seed']}" for run in failed))
    return "\n".join(lines) + "\n"


def finish(result, out):
    out.mkdir(parents=True, exist_ok=True)
    (out / "differential-report.json").write_text(json.dumps(result, indent=2, default=str), encoding="utf-8")
    markdown = render(result)
    (out / "differential-report.md").write_text(markdown, encoding="utf-8")
    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary:
        with open(summary, "a", encoding="utf-8") as handle:
            handle.write(markdown)
    print(markdown)
    if result["status"] == "skipped":
        prefix = "::warning::" if os.environ.get("GITHUB_ACTIONS") == "true" else "WARNING: "
        print(f"{prefix}Differential run skipped: {result['reason']}")
    return 1 if result["status"] == "failed" else 0


def summarize(summary, rev, runs=None):
    return {"rev": rev, "runs": summary["runs"], "withOutcomes": summary["withOutcomes"],
            "operations": len(summary["ops"]), "markersHit": sum(1 for v in summary["markers"].values() if v),
            "fuzzRuns": runs or []}


def judge(base, head, entries, settings, info):
    differences = compare(base, head, settings)
    unused = apply_manifest(differences, entries, base, head)
    failing = [d for d in differences if d["severity"] == "fail" and not d.get("intended")]
    failing += [entry for entry in unused if entry["severity"] == "fail"]
    return {"status": "failed" if failing else "passed", "differences": differences, "unusedEntries": unused,
            "manifest": entries, **info}


def parse(argv):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--base", help="Base revision (default: merge-base of --head and origin/dev)")
    parser.add_argument("--head", default="HEAD", help="Head revision (default HEAD)")
    parser.add_argument("--base-tree", help="Use this existing worktree for the base instead of checking out --base")
    parser.add_argument("--head-tree", help="Use this existing worktree for the head (default: a checkout of --head)")
    parser.add_argument("--base-runs", nargs="+", help="Compare existing run directories instead of running")
    parser.add_argument("--head-runs", nargs="+", help="Run directories of the head (with --base-runs)")
    parser.add_argument("--manifest", help="Intended-changes JSON to use (default: entries added between base and head)")
    parser.add_argument("--targets", default=DEFAULT_TARGETS, help="Comma-separated fuzz targets")
    parser.add_argument("--seeds", help="Comma-separated seeds")
    parser.add_argument("--pr", type=int, help="Derive --seed-count fixed seeds from this PR number")
    parser.add_argument("--seed-count", type=int, default=2)
    parser.add_argument("--count", type=int, default=30)
    parser.add_argument("--framework", default="net8.0")
    parser.add_argument("--run-timeout", type=int, default=600, help="Seconds per fuzz invocation")
    parser.add_argument("--no-build", action="store_true", help="Trees are already built")
    parser.add_argument("--out", default="artifacts_temp/differential", help="Report and run directory")
    parser.add_argument("--latency-relative", type=float, default=0.5)
    parser.add_argument("--latency-floor-ms", type=float, default=5.0)
    parser.add_argument("--latency-hard-factor", type=float, default=3.0)
    parser.add_argument("--latency-min-samples", type=int, default=20)
    parser.add_argument("--outcome-share", type=float, default=0.10)
    parser.add_argument("--closed-clean-tolerance", type=float, default=0.0)
    args = parser.parse_args(argv)
    if bool(args.base_runs) != bool(args.head_runs):
        parser.error("--base-runs and --head-runs go together")
    seeds = [int(seed) for seed in args.seeds.split(",")] if args.seeds else \
        pr_seeds(args.pr, args.seed_count) if args.pr is not None else [2947, 102947][:args.seed_count]
    args.seed_list, args.target_list = seeds, [t for t in args.targets.split(",") if t]
    return args


def load_manifest(args, base_rev, head_rev, head_tree):
    if args.manifest:
        data = json.loads(Path(args.manifest).read_text(encoding="utf-8"))
        return data.get("changes", []) if isinstance(data, dict) else data
    if not base_rev:
        return []
    head = common.Tree(common.WORKTREE, cwd=str(head_tree)) if head_tree else common.Tree(head_rev)
    return intended.added_entries(common.Tree(base_rev), head)


def main(argv=None):
    args = parse(argv)
    out = Path(args.out).resolve()
    info = {"settings": {"targets": args.target_list, "seeds": args.seed_list, "count": args.count,
                         **{name: getattr(args, name) for name in ("latency_relative", "latency_floor_ms",
                            "latency_hard_factor", "latency_min_samples", "outcome_share", "closed_clean_tolerance")}}}
    if args.base_runs:
        base, head = collect(args.base_runs), collect(args.head_runs)
        info.update(base=summarize(base, args.base or "base runs"),
                    head=summarize(head, "head runs" if args.head == "HEAD" else args.head))
        return finish(judge(base, head, load_manifest(args, None, None, None), args, info), out)
    base_rev = None if args.base_tree else (args.base or common.merge_base(args.head))
    workdir = Path(tempfile.mkdtemp(prefix="differential-"))
    created = []
    try:
        base_tree = Path(args.base_tree) if args.base_tree else checkout(base_rev, workdir, "base")
        head_tree = Path(args.head_tree) if args.head_tree else checkout(args.head, workdir, "head")
        created = [tree for tree in (base_tree, head_tree) if workdir in tree.parents]
        revs = {side: common.git("rev-parse", "HEAD", cwd=str(tree)).strip()
                for side, tree in (("base", base_tree), ("head", head_tree))}
        for side, tree in (("base", base_tree), ("head", head_tree)):
            if not has_harness(tree):
                reason = (f"the {side} tree ({revs[side][:12]}) lacks the outcome-emitting fuzz harness "
                          f"(no {HARNESS_FILE} writer in LiteDB.Fuzz); compare trees that both contain it, or "
                          "apply the harness commits to the old tree and pass it with --base-tree/--head-tree")
                return finish({"status": "skipped", "reason": reason, **info}, out)
        requested = {(target, seed) for target in args.target_list for seed in args.seed_list}
        sides = {}
        for side, tree in (("base", base_tree), ("head", head_tree)):
            if not args.no_build and build(tree, args.framework, out / f"build-{side}.log"):
                raise SystemExit(f"Building the {side} tree failed; see {out / f'build-{side}.log'}")
            shutil.rmtree(out / side, ignore_errors=True)
            runs = run_targets(tree, args.framework, args.target_list, args.seed_list, args.count, out / side,
                               args.run_timeout)
            sides[side] = (collect([out / side], requested), runs)
        info.update(base=summarize(sides["base"][0], revs["base"], sides["base"][1]),
                    head=summarize(sides["head"][0], revs["head"], sides["head"][1]))
        entries = load_manifest(args, base_rev or revs["base"], args.head, args.head_tree)
        return finish(judge(sides["base"][0], sides["head"][0], entries, args, info), out)
    finally:
        for tree in created:
            common.git("worktree", "remove", "--force", str(tree))
        shutil.rmtree(workdir, ignore_errors=True)


if __name__ == "__main__":
    sys.exit(main())
