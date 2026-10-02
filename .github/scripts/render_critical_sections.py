"""Render a critical PR's "Critical change evidence" section from the change's artifacts.

The subsections check_pr_section.py requires for a critical change, generated rather
than written: Intended changes (the entries the change adds to intended-changes.json),
Markers declared (every marker the diff declares, with the hit counts of a campaign's
reachability.json from check_reachability.py --runs ... --output), Proofs (the proof
entries the change adds, plus --proofs rows), Blast radius (--blast-radius rows) and
Benchmark diff (--benchmark report or --no-hot-path reason). What no artifact supplies is
rendered as a TODO line that check_pr_section.py rejects, never as a passing default.

  --proofs        JSON list of {"knownBad": sha, "net": name, "seed": n, "failureId": id, "artifact": link}
  --blast-radius  JSON list of {"contract": method, "caller": caller, "disposition": text}
"""
import argparse
import json
import sys
from pathlib import Path

import check_pr_section as section
import safety_common as common

TODO = "TODO: {what} (see docs/rules/implement-safely.md)."


def intended(context):
    entries = context["manifest"]
    if not entries:
        return ["None: this change adds no entry to .github/safety/intended-changes.json, so the differential "
                "run must show no unclaimed difference."]
    return [f"- `{item.get('call')}` {('[' + item['dimension'] + '] ') if item.get('dimension') else ''}"
            f"{item.get('change')}: {item.get('before')} → {item.get('after')} ({item.get('doc')})" for item in entries]


def markers(context, reachability):
    if not context["markers"]:
        return ["None: the diff declares no reachability marker (check_reachability.py --base)."]
    if reachability is None:
        return [TODO.format(what="run the smoke campaign and pass its reachability.json with --reachability")]
    counted = reachability.get("markers", {})
    rows = ["| Marker | Hits | Targets | Gate | Declared because |", "| --- | ---: | --- | --- | --- |"]
    for name, marker in context["markers"].items():
        hit = counted.get(name, {})
        targets = ", ".join(f"{target} {count}" for target, count in sorted(hit.get("targets", {}).items()))
        rows.append(f"| `{name}` | {hit.get('hits', 0)} | {targets or '-'} | {marker['gate']} | "
                    f"{'; '.join(marker['reasons'])} |")
    return rows


def proofs(context, rows, head_tree):
    lines = []
    ledger = {item.get("id"): item for item in (head_tree.read_json(section.NET_PROOFS, {}) or {}).get("proofs", [])}
    repros = {item.get("repro"): item for item in
              (head_tree.read_json(section.REGRESSION_PROOFS, {}) or {}).get("proofs", [])}
    for name in context["proofs"]:
        if name in ledger:
            entry, results = ledger[name], ledger[name].get("results") or {}
            recorded = results.get("recorded") or (entry.get("net") or {}).get("recorded")
            # A recorded entry ran outside net_proof.py; its result lives in the ledger, it is not pending.
            pending = f"recorded (see {section.NET_PROOFS})" if recorded else "not run yet"
            assertion = (results.get("knownBad") or {}).get("assertion") or pending
            lines.append(f"- `{entry['knownBad']['commit'][:12]}` → {entry['net']['name']} → {assertion} "
                         f"(net proof `{name}`, {entry.get('level')}, {results.get('state', 'not attempted')})")
        else:
            bad = repros.get(name, {}).get("knownBad", {})
            state = bad.get("commit", "")[:12] or bad.get("version", "?")
            lines.append(f"- `{state}` → regression proof (ReproRunner) → repro `{name}`")
    for row in rows:
        lines.append(f"- `{str(row.get('knownBad', ''))[:12]}` → {row.get('net')} → seed {row.get('seed')}"
                     + (f" ({row['failureId']})" if row.get("failureId") else "")
                     + (f", {row['artifact']}" if row.get("artifact") else ""))
    return lines or ["None: no finding was fixed and no proof entry was added by this change."]


def blast_radius(rows):
    if rows is None:
        return [TODO.format(what="list every caller of each widened contract with its disposition "
                                 "(blast-radius step), or say why no contract widens")]
    if not rows:
        return ["None: no method's contract widens (no new throw, no removed catch, no new exception type)."]
    return [f"- `{row.get('contract')}` → `{row.get('caller')}` → {row.get('disposition')}" for row in rows]


def benchmark(path, reason):
    if reason:
        return [f"No hot path touched: {reason}"]
    if path:
        return Path(path).read_text(encoding="utf-8").strip().splitlines()[:40]
    return [TODO.format(what="attach the contention diff with its tolerance, or say why no hot path is touched")]


def render(context, head_tree, reachability=None, proof_rows=(), blast=None, bench=None, no_hot_path=None):
    parts = [("Intended changes", intended(context)), ("Markers declared", markers(context, reachability)),
             ("Proofs", proofs(context, list(proof_rows), head_tree)), ("Blast radius", blast_radius(blast)),
             ("Benchmark diff", benchmark(bench, no_hot_path))]
    text = ["## Critical change evidence", ""]
    for name, lines in parts:
        text += [f"### {name}", ""] + lines + [""]
    return "\n".join(text)


def _json(path):
    return None if path is None else common.load_json_file(path)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--base", required=True, help="The merge-base the change is judged against")
    parser.add_argument("--head", default="HEAD")
    parser.add_argument("--reachability", help="reachability.json of the campaign (check_reachability.py --output)")
    parser.add_argument("--proofs", help="JSON list of finding proofs (knownBad, net, seed, failureId, artifact)")
    parser.add_argument("--blast-radius", help="JSON list of {contract, caller, disposition}; [] = nothing widens")
    parser.add_argument("--benchmark", help="Markdown/text benchmark diff to include")
    parser.add_argument("--no-hot-path", help="Why no wait primitive, scheduler or hot path changed")
    parser.add_argument("--output", help="Write the markdown here (default: stdout)")
    args = parser.parse_args(argv)
    context = section.critical_context(args.base, args.head)
    text = render(context, common.Tree(args.head), _json(args.reachability), _json(args.proofs) or [],
                  _json(args.blast_radius), args.benchmark, args.no_hot_path)
    if args.output:
        Path(args.output).write_text(text, encoding="utf-8")
    else:
        print(text)
    return 0


if __name__ == "__main__":
    sys.exit(main())
