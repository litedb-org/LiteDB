"""Differential run: the same fuzz seeds on the merge-base and the head, NORMALIZED outcomes compared.

Each tree is built in its own worktree (LiteDB.Fuzz, Release, TestingEnabled=true) and
runs ITS OWN harness on the same targets, seeds and counts. Raw traces, timings and the
order of legal concurrent winners vary between runs, so only normalized outcomes are
compared, per operation class and dimension (`op`, `dimension` in outcomes.jsonl):

- permitted outcomes: the SET of outcome kinds observed (ok/threw/refused/hang); an
  outcome outside a record's declared `permitted` set ('ok', 'threw', 'threw:Type',
  'threw:Type#code') fails on its own, and when both trees declare the same permitted
  set for a racing operation, which permitted outcomes a run happened to observe is a
  permitted variation, not a difference;
- exception contract: the set of exception types (+ errorCode) that escape, and whether
  the primary failure is preserved (`primaryExceptionType` -> escaped type pairs);
- payloads and acknowledged effects: multisets of `payloadDigest` / `effectsDigest`;
- cleanup obligations: per ConnectionClean/Quiescent/ScratchLive (or legacy ClosedClean)
  evaluation, an unclean result or a violation kind the base never showed;
- reachability: a marker the base reached and the head never reaches.

No latency: performance is separate, class-3 evidence (paired repeated measurements).
Concurrent targets are native-thread evidence (class 2): the outcome set of a racing
operation can vary between runs of one tree. With --repeat N each tree runs every seed N
times; an operation whose outcome or exception set varies between repeats of the SAME
tree is classified schedule-dependent, and its cross-tree differences are reported in
that class instead of failing. With one repeat there is no such evidence and every
difference counts.
Optional fields absent on either side are reported as "not compared", never as passed
(field contract: /tmp/safety-net/contracts/m4-outcomes.md, summarized in
docs/rules/safety-evidence.md). Operation classes and markers that exist only on the head
are new capabilities: they are listed, not diffed.

Every remaining difference fails unless an entry ADDED to .github/safety/intended-changes.json
by this change covers it (check_intended_changes.py); an added entry whose change is not
observed fails too, because the PR claims a contract change nobody exercised. While
net-modes.json says the nets are advisory, findings are reported and the exit code is 0.

Both trees must contain the outcome-emitting harness (`outcomes.jsonl` written by
LiteDB.Fuzz); every PR based on a dev that includes it qualifies. When the base lacks it
the run is SKIPPED with a visible warning, never passed silently. To compare older trees,
create a worktree of the old commit, apply the harness commits on top (cherry-pick, as
the net proofs do) and pass it with --base-tree/--head-tree.
"""
import argparse
import json
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
OBLIGATIONS = {"connection-clean.jsonl": "ConnectionClean", "quiescent.jsonl": "Quiescent",
               "scratch-live.jsonl": "ScratchLive", "closed-clean.jsonl": "ClosedClean"}
OPTIONAL = {"payloadDigest": "payloads", "effectsDigest": "effects", "primaryExceptionType": "primary"}
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
    """Run every (target, seed) once below `out`; return [{target, seed, exitCode, directory}]."""
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


def _new_op():
    return {"outcomes": Counter(), "exceptions": Counter(), "primary": Counter(), "payloads": Counter(),
            "effects": Counter(), "permitted": set(), "unpermitted": Counter(), "records": 0,
            "present": Counter()}


def _exception(name, code):
    return name + (f"#{code}" if code is not None else "") if name else "none"


def collect(roots, requested=None):
    """Aggregate normalized outcomes of every run below roots; requested = {(target, seed)} drops corpus replays."""
    summary = {"ops": defaultdict(_new_op), "obligations": defaultdict(lambda: {"evaluations": 0, "unclean": 0,
               "violations": Counter()}), "markers": Counter(), "runs": 0, "withOutcomes": 0}
    for root in roots:
        for run_json in sorted(Path(root).rglob("run.json")):
            directory = run_json.parent
            meta = json.loads(run_json.read_text(encoding="utf-8"))
            if requested is not None and (meta.get("target"), meta.get("seed")) not in requested:
                continue
            summary["runs"] += 1
            summary["withOutcomes"] += (directory / HARNESS_FILE).is_file()
            for record in _jsonl(directory / HARNESS_FILE):
                _add_outcome(summary["ops"][(record.get("op"), record.get("dimension") or "")], record)
            for name, oracle in OBLIGATIONS.items():
                for record in _jsonl(directory / name):
                    key = (oracle, record.get("op") or oracle, record.get("dimension") or "")
                    entry = summary["obligations"][key]
                    entry["evaluations"] += 1
                    entry["unclean"] += record.get("clean") is False
                    entry["violations"].update(_violation_kind(item) for item in record.get("violations") or [])
            markers = directory / "markers.json"
            if markers.is_file():
                summary["markers"].update(json.loads(markers.read_text(encoding="utf-8")).get("hits", {}))
    return summary


def _add_outcome(entry, record):
    entry["records"] += 1
    entry["outcomes"][record.get("outcome")] += 1
    escaped = _exception(record.get("exceptionType"), record.get("errorCode"))
    if record.get("exceptionType"):
        entry["exceptions"][escaped] += 1
    for field, bucket in OPTIONAL.items():
        if record.get(field) is not None:
            entry["present"][bucket] += 1
            entry[bucket][f"{record[field]} -> {escaped}" if bucket == "primary" else record[field]] += 1
    if isinstance(record.get("permitted"), list):
        entry["permitted"].update(record["permitted"])
        if not permitted(record, record["permitted"]):
            entry["unpermitted"][f"{record.get('outcome')}:{escaped}"] += 1


def permitted(record, allowed):
    """An outcome is permitted by its kind ('threw'), or by kind and type ('threw:T', 'threw:T#code')."""
    outcome, escaped = record.get("outcome"), _exception(record.get("exceptionType"), record.get("errorCode"))
    return any(item in allowed for item in (outcome, f"{outcome}:{escaped.split('#')[0]}", f"{outcome}:{escaped}"))


def _violation_kind(item):
    text = item.get("kind") if isinstance(item, dict) else str(item)
    return str(text).split(":", 1)[0].strip() or "unspecified"


# --- comparing -------------------------------------------------------------

def instability(repeats):
    """(op, dimension) keys whose outcome or exception set differs between repeats of one tree."""
    unstable = set()
    for key in set().union(*(summary["ops"] for summary in repeats)) if repeats else ():
        shapes = {(frozenset(summary["ops"][key]["outcomes"]), frozenset(summary["ops"][key]["exceptions"]))
                  for summary in repeats if key in summary["ops"]}
        if len(shapes) > 1:
            unstable.add(key)
    return unstable


SCHEDULE_SENSITIVE = {"outcome-change", "new-exception", "exception-removed"}


def compare(base, head, unstable=frozenset()):
    """Return (differences, capabilities, not_compared)."""
    found, capabilities, not_compared = [], {"operations": [], "markers": []}, {"absent": Counter(), "partial": []}

    def add(kind, op, dimension, detail, **extra):
        found.append({"kind": kind, "op": op, "dimension": dimension, "detail": detail, **extra})

    for side, summary in (("base", base), ("head", head)):
        if not summary["withOutcomes"]:  # an empty comparison is not a pass
            add("harness", "-", "", f"no {side} run wrote {HARNESS_FILE} ({summary['runs']} runs): the selected "
                "targets do not record outcomes, so nothing was compared", coverable=False)
    for key in sorted(set(base["ops"]) | set(head["ops"]), key=str):
        op, dimension = key
        if key not in base["ops"]:
            capabilities["operations"].append(f"{op} [{dimension or '-'}]")
            continue
        if key not in head["ops"]:
            add("outcome-change", op, dimension, "operation class no longer exercised on the head")
            continue
        before = len(found)
        _compare_op(add, not_compared, op, dimension, base["ops"][key], head["ops"][key])
        old, new = base["ops"][key], head["ops"][key]
        declared = old["permitted"] and old["permitted"] == new["permitted"] \
            and not old["unpermitted"] and not new["unpermitted"]
        for difference in found[before:]:
            if difference["kind"] in SCHEDULE_SENSITIVE and declared:
                difference["class"] = "permitted-variation"
            elif difference["kind"] in SCHEDULE_SENSITIVE and key in unstable:
                difference["class"] = "schedule-dependent"
    for oracle, op, dimension in sorted(set(base["obligations"]) | set(head["obligations"])):
        old = base["obligations"].get((oracle, op, dimension))
        new = head["obligations"].get((oracle, op, dimension))
        if new and new["unclean"] and not (old and old["unclean"]):
            add("cleanup-change", op, dimension, f"{oracle}: {new['unclean']}/{new['evaluations']} evaluations "
                "unclean, none on the base", oracle=oracle)
        for violation in sorted(set(new["violations"] if new else ()) - set(old["violations"] if old else ())):
            add("cleanup-change", op, dimension, f"{oracle}: new violation '{violation}'", oracle=oracle)
    for marker in sorted(set(base["markers"]) | set(head["markers"])):
        if base["markers"][marker] and not head["markers"][marker]:
            add("marker", marker, "", f"reached {base['markers'][marker]}x on the base, never on the head")
        elif head["markers"][marker] and not base["markers"][marker]:
            capabilities["markers"].append(marker)
    return found, capabilities, not_compared


def _compare_op(add, not_compared, op, dimension, old, new):
    _compare_contract(add, op, dimension, old, new)
    _compare_optional(add, not_compared, op, dimension, old, new)


def _compare_contract(add, op, dimension, old, new):
    for outcome, count in sorted(new["unpermitted"].items()):
        add("outcome-not-permitted", op, dimension, f"'{outcome}' {count}x outside the declared permitted set "
            f"{sorted(new['permitted'])}", coverable=False)
    if set(old["outcomes"]) != set(new["outcomes"]):
        add("outcome-change", op, dimension, f"outcomes {sorted(old['outcomes'])} -> {sorted(new['outcomes'])}",
            kinds=sorted(set(old["outcomes"]) ^ set(new["outcomes"])))
    if old["permitted"] and new["permitted"] and old["permitted"] != new["permitted"]:
        add("outcome-change", op, dimension, f"permitted set {sorted(old['permitted'])} -> {sorted(new['permitted'])}")
    for exception in sorted(set(new["exceptions"]) - set(old["exceptions"])):
        add("new-exception", op, dimension, f"escapes {exception} ({new['exceptions'][exception]}x)",
            exception=exception)
    for exception in sorted(set(old["exceptions"]) - set(new["exceptions"])):
        add("exception-removed", op, dimension, f"no longer escapes {exception}", exception=exception)


def _compare_optional(add, not_compared, op, dimension, old, new):
    for bucket, kind in (("primary", "primary-changed"), ("payloads", "payload-change"), ("effects", "effect-change")):
        if old["present"][bucket] < old["records"] or new["present"][bucket] < new["records"]:
            if old["present"][bucket] or new["present"][bucket]:
                not_compared["partial"].append(f"{op} [{dimension or '-'}]: {bucket} recorded for "
                                               f"{old['present'][bucket]}/{old['records']} base and "
                                               f"{new['present'][bucket]}/{new['records']} head calls")
            else:
                not_compared["absent"][bucket] += 1
            continue
        if bucket == "primary":
            replaced = {pair for pair in new["primary"] if pair.split(" -> ")[0] != pair.split(" -> ")[1].split("#")[0]}
            for pair in sorted(replaced - set(old["primary"])):
                add(kind, op, dimension, f"primary failure not preserved: {pair}", exception=pair.split(" -> ")[1])
        elif old[bucket] != new[bucket]:
            add(kind, op, dimension, f"{bucket} differ: {sum((old[bucket] - new[bucket]).values())} base-only, "
                f"{sum((new[bucket] - old[bucket]).values())} head-only digests")


def apply_manifest(differences, entries, base, head):
    """Mark differences an entry covers; return entries that cover nothing observed."""
    used = set()
    for difference in differences:
        if difference.get("coverable") is False:
            continue
        for index, entry in enumerate(entries):
            named = difference.get("exception", "").split("#")[0]
            side = entry.get("before") if difference["kind"] == "exception-removed" else entry.get("after")
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
    observed = {op for op, _ in set(base["ops"]) & set(head["ops"])} | set(base["markers"]) \
        | {op for _, op, _ in set(base["obligations"]) & set(head["obligations"])}
    for index, entry in enumerate(entries):
        if index not in used:
            unused.append({**entry, "why": "claimed change not observed" if entry.get("call") in observed
                           else "claimed call not exercised"})
    return unused


# --- reporting -------------------------------------------------------------

def render(result):
    status = result["status"]
    lines = [f"## Differential run: {status}", ""]
    if status == "skipped":
        return "\n".join(lines + [f"> **Skipped:** {result['reason']}", ""])
    if result.get("advisory") and result.get("wouldFail"):
        lines += ["> Advisory mode (`.github/safety/net-modes.json`): the findings below would fail the run once "
                  "the diff nets are made blocking.", ""]
    lines += [f"Base `{result['base']['rev']}` vs head `{result['head']['rev']}`; targets "
              f"{', '.join(result['settings']['targets'])}; seeds {result['settings']['seeds']}; "
              f"count {result['settings']['count']}. Runs with outcomes: base "
              f"{result['base']['withOutcomes']}/{result['base']['runs']}, head "
              f"{result['head']['withOutcomes']}/{result['head']['runs']}. Normalized outcomes only; no latency.", ""]
    if result["differences"]:
        lines += ["| Change | Operation | Dimension | Observed | Intended by |", "| --- | --- | --- | --- | --- |"]
        lines += [f"| {d['kind']} | `{d['op']}` | {d['dimension'] or '-'} | {d['detail']} | "
                  f"{d.get('intended') or _verdict(d)} |"
                  for d in result["differences"]]
    else:
        lines.append("No normalized behavior difference.")
    if result.get("unstable"):
        lines += ["", f"Schedule-dependent (outcome set varied between repeats of one tree, "
                  f"{result['settings'].get('repeat', 1)} per tree): "
                  + ", ".join(f"`{item}`" for item in result["unstable"]) + "."]
    for entry in result["unusedEntries"]:
        lines.append(f"- **{entry['why']}**: `{entry.get('call')}` {entry.get('change')} ({entry.get('doc')})")
    capabilities = result["capabilities"]
    lines += ["", "**Capabilities only on the head** (listed, not diffed): operations "
              + (", ".join(f"`{item}`" for item in capabilities["operations"]) or "none") + "; markers "
              + (", ".join(f"`{item}`" for item in capabilities["markers"]) or "none") + "."]
    absent, partial = result["notCompared"]["absent"], result["notCompared"]["partial"]
    if absent:
        lines += ["", "**Not compared** (the harness records no such field on either side): "
                  + ", ".join(f"{bucket} for {count} operation class(es)" for bucket, count in sorted(absent.items()))
                  + "."]
    if partial:
        lines += ["", "<details><summary>Not compared (field recorded on one side only)</summary>", ""]
        lines += [f"- {item}" for item in partial] + ["", "</details>"]
    for side in ("base", "head"):
        failed = [run for run in result[side].get("fuzzRuns", []) if run["exitCode"] != 0]
        if failed:
            lines.append(f"- {side}: {len(failed)} fuzz run(s) failed; their outcomes are truncated: "
                         + ", ".join(f"{run['target']}/{run['seed']}" for run in failed))
    return "\n".join(lines) + "\n"


def _verdict(difference):
    if difference.get("class") == "schedule-dependent":
        return "schedule-dependent (varies between repeats of one tree; not failing)"
    if difference.get("class") == "permitted-variation":
        return "permitted variation (both trees declare the same permitted outcomes; not failing)"
    return "**not coverable**" if difference.get("coverable") is False else "**not in manifest**"


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
    prefix = "::warning::" if os.environ.get("GITHUB_ACTIONS") == "true" else "WARNING: "
    if result["status"] == "skipped":
        print(f"{prefix}Differential run skipped: {result['reason']}")
    elif result.get("wouldFail") and result.get("advisory"):
        print(f"{prefix}Differential run found {result['wouldFail']} unexplained difference(s) (advisory)")
    return 1 if result["status"] == "failed" else 0


def summarize(summary, rev, runs=None):
    return {"rev": rev, "runs": summary["runs"], "withOutcomes": summary["withOutcomes"],
            "operations": len(summary["ops"]), "markersHit": sum(1 for v in summary["markers"].values() if v),
            "fuzzRuns": runs or []}


def judge(base, head, entries, advisory, info, unstable=frozenset()):
    differences, capabilities, not_compared = compare(base, head, unstable)
    unused = apply_manifest(differences, entries, base, head)
    failing = len([d for d in differences if not d.get("intended") and not d.get("class")])
    failing += len(unused)
    info["unstable"] = sorted(f"{op} [{dimension or '-'}]" for op, dimension in unstable)
    status = "passed" if not failing else "advisory" if advisory else "failed"
    return {"status": status, "advisory": advisory, "wouldFail": failing, "differences": differences,
            "unusedEntries": unused, "capabilities": capabilities, "notCompared": not_compared,
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
    parser.add_argument("--repeat", type=int, default=1,
                        help="Runs per seed and tree; >1 classifies schedule-dependent operations")
    parser.add_argument("--framework", default="net8.0")
    parser.add_argument("--run-timeout", type=int, default=600, help="Seconds per fuzz invocation")
    parser.add_argument("--no-build", action="store_true", help="Trees are already built")
    parser.add_argument("--out", default="artifacts_temp/differential", help="Report and run directory")
    common.add_mode_arguments(parser)
    args = parser.parse_args(argv)
    if bool(args.base_runs) != bool(args.head_runs):
        parser.error("--base-runs and --head-runs go together")
    seeds = [int(seed) for seed in args.seeds.split(",")] if args.seeds else \
        pr_seeds(args.pr, args.seed_count) if args.pr is not None else [2947, 102947][:args.seed_count]
    args.seed_list, args.target_list = seeds, [t for t in args.targets.split(",") if t]
    args.advisory = common.net_advisory("differential-run", args.blocking)
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
    out.mkdir(parents=True, exist_ok=True)
    info = {"settings": {"targets": args.target_list, "seeds": args.seed_list, "count": args.count,
                         "repeat": args.repeat}}
    requested = {(target, seed) for target in args.target_list for seed in args.seed_list} \
        if (args.seeds or args.pr is not None or not args.base_runs) else None
    if args.base_runs:  # each directory given is one repeat of that tree
        repeats = [collect([root], requested) for root in args.base_runs + args.head_runs]
        base, head = collect(args.base_runs, requested), collect(args.head_runs, requested)
        info.update(base=summarize(base, args.base or "base runs"),
                    head=summarize(head, "head runs" if args.head == "HEAD" else args.head))
        unstable = instability(repeats[:len(args.base_runs)]) | instability(repeats[len(args.base_runs):])
        return finish(judge(base, head, load_manifest(args, None, None, None), args.advisory, info, unstable), out)
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
        sides, unstable = {}, set()
        for side, tree in (("base", base_tree), ("head", head_tree)):
            if not args.no_build and build(tree, args.framework, out / f"build-{side}.log"):
                raise SystemExit(f"Building the {side} tree failed; see {out / f'build-{side}.log'}")
            shutil.rmtree(out / side, ignore_errors=True)
            runs = []
            for repeat in range(args.repeat):
                runs += run_targets(tree, args.framework, args.target_list, args.seed_list, args.count,
                                    out / side / f"r{repeat}", args.run_timeout)
            repeats = [collect([out / side / f"r{repeat}"], requested) for repeat in range(args.repeat)]
            unstable |= instability(repeats)
            sides[side] = (collect([out / side], requested), runs)
        info.update(base=summarize(sides["base"][0], revs["base"], sides["base"][1]),
                    head=summarize(sides["head"][0], revs["head"], sides["head"][1]))
        entries = load_manifest(args, base_rev or revs["base"], args.head, args.head_tree)
        return finish(judge(sides["base"][0], sides["head"][0], entries, args.advisory, info, unstable), out)
    finally:
        for tree in created:
            common.git("worktree", "remove", "--force", str(tree))
        shutil.rmtree(workdir, ignore_errors=True)


if __name__ == "__main__":
    sys.exit(main())
