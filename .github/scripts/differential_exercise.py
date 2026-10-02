"""Exercise accounting for the differential run (differential_run.py).

A difference can only be observed where both trees exercised the same thing, so every
comparison key carries its exercise count on both sides and ends in one of these states:

- `changed`: exercised on both sides and a difference was found;
- `unchanged`: exercised on both sides, no difference;
- `not-exercised-base` / `not-exercised-head`: only one side exercised it (a key only the
  head exercises is a capability; one only the base exercises is "no longer exercised");
- `not-exercised`: neither side (only for manifest entries, which name keys up front).

Keys: `(op, dimension)` for outcomes.jsonl, `(path, site, model)` for FaultDisposed rows.
A manifest entry is matched against the keys its `call` (and optional `dimension` pattern)
names, so an unused entry says whether its claim was exercised and unchanged or never
exercised, and on which side.

Early stops (stopped_before, stop_notes) follow the rule stated in differential_run.py.
"""
import fnmatch

import differential_collect as records

STATES = ("changed", "unchanged", "not-exercised-base", "not-exercised-head")
OP_KINDS = {"outcome-change", "outcome-not-permitted", "new-exception", "exception-removed", "primary-changed",
            "payload-change", "effect-change"}


def _state(old, new, changed):
    if old and new:
        return "changed" if changed else "unchanged"
    return "not-exercised-head" if old else "not-exercised-base"


def op_rows(base, head, differences, truncated=frozenset()):
    """Exercise counts (records per side) and state of every (op, dimension)."""
    changed = {(item["op"], item["dimension"]) for item in differences if item["kind"] in OP_KINDS}
    rows = []
    for op, dimension in sorted(set(base["ops"]) | set(head["ops"]), key=str):
        old = base["ops"][(op, dimension)]["records"] if (op, dimension) in base["ops"] else 0
        new = head["ops"][(op, dimension)]["records"] if (op, dimension) in head["ops"] else 0
        row = {"op": op, "dimension": dimension, "base": old, "head": new,
               "state": _state(old, new, (op, dimension) in changed)}
        rows.append({**row, "stoppedEarly": True} if (op, dimension) in truncated else row)
    return rows


def stopped_before(first, other_runs):
    """Ids of the other side's runs whose early stop explains a key's absence there, else None.

    first maps each run id in which this side saw the key to the first step it appeared at.
    """
    ids = []
    for run_id, step in sorted(first.items(), key=str):
        run = other_runs.get(run_id)
        if run is None or run["stop"] is None or step < run["stop"]:
            return None
        ids.append(run_id)
    return ids or None


def stop_notes(explained, base, head):
    """One note per head run whose early stop explains base-only operation classes.

    explained maps (op, dimension) -> head run ids (stopped_before). A note fails when the
    base run of the same schedule completed every requested step.
    """
    keys_by_run = {}
    for key, run_ids in explained.items():
        for run_id in run_ids:
            keys_by_run.setdefault(run_id, []).append(key)
    notes = []
    for run_id, keys in sorted(keys_by_run.items(), key=str):
        run, other = head["runList"][run_id], base["runList"].get(run_id) or {}
        notes.append({"target": run["target"], "seed": run["seed"], "repeat": run["repeat"], "stopStep": run["stop"],
                      "status": run["status"], "failureId": run["failureId"], "directory": run["directory"],
                      "baseStop": other.get("stop"), "baseStatus": other.get("status"), "keys": len(keys),
                      "examples": [f"{op} [{dimension or '-'}]" for op, dimension in sorted(keys)[:3]],
                      "failing": other.get("stop") is None and run_id in base["runList"]})
    return notes


def compare_dispositions(add, base, head, unstable=frozenset()):
    """Compare FaultDisposed rows per (path, site, model); return (capabilities, exercise rows).

    At a site both trees exercised, a declared -> observed pair new on the head is a
    cleanup-change, even when the base showed that pair at another site of the path. At a
    site only the head exercised there is nothing to compare it with, so the path-level
    rule applies: a pair the base never showed on that path is a cleanup-change. A path the
    base never exercised at all (and that is no base operation class) is a capability.
    """
    base_ops = {op for op, _ in base["ops"]}
    capabilities, rows = [], []
    for path in sorted(set(base["dispositions"]) | set(head["dispositions"])):
        old_sites, new_sites = base["dispositions"].get(path, {}), head["dispositions"].get(path, {})
        if not old_sites and path not in base_ops:
            capabilities += [f"{path}: {pair}" for pair in sorted(records.path_pairs(new_sites))]
            rows += [_row(path, site, None, new_sites[site], "not-exercised-base") for site in sorted(new_sites)]
            continue
        path_pairs = records.path_pairs(old_sites)
        for site in sorted(set(old_sites) | set(new_sites)):
            old, new = old_sites.get(site), new_sites.get(site)
            fresh = sorted(set(new["pairs"]) - set(old["pairs"] if old else path_pairs)) if new else []
            for pair in fresh:
                where = (f"at this site (base here: {', '.join(sorted(old['pairs']))})" if old else
                         "on this path; the base never exercised this site")
                extra = {"class": "schedule-dependent"} if ("disposition", path, site) in unstable else {}
                add("cleanup-change", path, records.site_dimension(*site),
                    f"FaultDisposed: declared -> observed {pair} new on the head {where}", **extra)
            rows.append(_row(path, site, old, new, "changed" if fresh else _state(old, new, False)))
    return capabilities, rows


def _row(path, site, old, new, state):
    return {"path": path, "site": site[0], "model": site[1], "base": old["rows"] if old else 0,
            "head": new["rows"] if new else 0, "basePairs": sorted(old["pairs"]) if old else [],
            "headPairs": sorted(new["pairs"]) if new else [], "state": state}


def summarize_states(rows):
    counts = {state: 0 for state in STATES}
    for row in rows:
        counts[row["state"]] += 1
    return counts


def entry_status(entry, base, head, used, truncated=frozenset()):
    """Exercise counts of the keys a manifest entry names and its state; `why` for an unused entry."""
    change, call, pattern = entry.get("change"), entry.get("call"), entry.get("dimension")

    def named(dimension):
        return not pattern or fnmatch.fnmatchcase(dimension or "", pattern)

    stopped = False
    if change == "marker":
        unit = "marker"
        fault = call[len("fault-point:"):] if str(call).startswith("fault-point:") else None
        counts = [(base["markers"].get(call, 0), head["markers"].get(call, 0))]
        counts += [(base["faults"].get(fault, 0), head["faults"].get(fault, 0))] if fault else []
    elif change == "cleanup-change":
        unit = "site or evaluation key"
        old_sites, new_sites = base["dispositions"].get(call, {}), head["dispositions"].get(call, {})
        counts = [(old_sites[site]["rows"] if site in old_sites else 0, new_sites[site]["rows"] if site in new_sites
                   else 0) for site in set(old_sites) | set(new_sites) if named(records.site_dimension(*site))]
        counts += [(base["obligations"][key]["evaluations"] if key in base["obligations"] else 0,
                    head["obligations"][key]["evaluations"] if key in head["obligations"] else 0)
                   for key in set(base["obligations"]) | set(head["obligations"]) if key[1] == call and named(key[2])]
    else:
        unit = "dimension"
        keys = [key for key in set(base["ops"]) | set(head["ops"]) if key[0] == call and named(key[1])]
        counts = [(base["ops"][key]["records"] if key in base["ops"] else 0,
                   head["ops"][key]["records"] if key in head["ops"] else 0) for key in keys]
        stopped = bool(keys) and all(key in truncated for key in keys)
    old, new = sum(item[0] for item in counts), sum(item[1] for item in counts)
    common = sum(1 for item in counts if item[0] and item[1])
    status = {"call": call, "dimension": pattern, "change": change, "base": old, "head": new}
    if entry.get("id"):
        status["id"] = entry["id"]
    if used:
        return {**status, "state": "changed", "why": "claimed change observed"}
    if old and new:
        state, why = "unchanged", f"claimed change not observed: exercised, unchanged (base {old}, head {new}"
        why += f"; never at the same {unit} on both sides)" if not common else ")"
    elif old or new:
        state = "not-exercised-head" if old else "not-exercised-base"
        why = f"claimed call not exercised on the {'head' if old else 'base'} (base {old}, head {new})"
        why += "; the head runs stopped before the steps where the base exercised it" if stopped else ""
    else:
        state, why = "not-exercised", "claimed call not exercised on either side"
    return {**status, "state": state, "why": why}


# --- reporting -------------------------------------------------------------

def render_stops(result):
    """One note per head run whose early stop explains base-only operation classes, then every early stop."""
    lines = []
    for note in (result.get("exercise") or {}).get("stopNotes", []):
        base = "completed" if note["baseStop"] is None else f"stopped at step {note['baseStop']}"
        lines.append(f"- `{note['target']}` seed {note['seed']} r{note['repeat']}: the head run stopped at step "
                     f"{note['stopStep']} ({note['failureId'] or note['status']}); the base run {base}. "
                     f"{note['keys']} operation class(es) the base exercised only at steps >= {note['stopStep']} are "
                     f"not compared, e.g. " + ", ".join(f"`{item}`" for item in note["examples"])
                     + (". **Fails**: the base run completed." if note["failing"] else "."))
    if lines:
        lines = ["", "**Not compared: head runs that stopped early** (one note per run; operation classes seen on "
                 "the base only after the step where the head run stopped):"] + lines
    for side in ("base", "head"):
        stopped = result.get(side, {}).get("stoppedEarly") or []
        if stopped:
            lines.append(f"- {side}: {len(stopped)} run(s) stopped before their requested count: "
                         + ", ".join(f"{run['target']}/{run['seed']}/r{run['repeat']}@{run['stop']}" for run in stopped))
    return lines


def render_exercise(result):
    """Exercise counts: which keys both trees exercised, changed or not, and which only one side reached."""
    exercised = result.get("exercise") or {}
    lines = []
    for label, rows in (("Operation classes", exercised.get("operations", [])),
                        ("FaultDisposed (path, site, model)", exercised.get("dispositions", []))):
        if rows:
            counts = summarize_states(rows)
            stopped = sum(1 for row in rows if row.get("stoppedEarly"))
            lines.append(f"- {label}: {counts['changed']} exercised and changed, {counts['unchanged']} exercised "
                         f"and unchanged, {counts['not-exercised-head']} not exercised on the head"
                         + (f" ({stopped} of them only after a head run stopped early)" if stopped else "")
                         + f", {counts['not-exercised-base']} not exercised on the base.")
    if exercised.get("dispositions"):
        lines += ["", "<details><summary>FaultDisposed exercise per teardown path, site and model</summary>", "",
                  "| Path | Site | Model | Base | Head | State | Base pairs | Head pairs |",
                  "| --- | --- | --- | --- | --- | --- | --- | --- |"]
        lines += [f"| `{row['path']}` | {row['site']} | {row['model']} | {row['base']} | {row['head']} | {row['state']} | "
                  f"{', '.join(row['basePairs']) or '-'} | {', '.join(row['headPairs']) or '-'} |"
                  for row in exercised["dispositions"]]
        lines += ["", "</details>"]
    statuses = result.get("manifestStatus") or []
    if statuses:
        lines += ["", "| Manifest entry | Change | Base | Head | State |", "| --- | --- | --- | --- | --- |"]
        lines += [f"| {status.get('id') or ''} `{status['call']}` {status.get('dimension') or ''} | {status['change']} | "
                  f"{status['base']} | {status['head']} | {status['state']} |" for status in statuses]
    return ["", "**Exercise** (records per key on each side):"] + lines if lines else []
