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


def op_rows(base, head, differences):
    """Exercise counts (records per side) and state of every (op, dimension)."""
    changed = {(item["op"], item["dimension"]) for item in differences if item["kind"] in OP_KINDS}
    rows = []
    for op, dimension in sorted(set(base["ops"]) | set(head["ops"]), key=str):
        old = base["ops"][(op, dimension)]["records"] if (op, dimension) in base["ops"] else 0
        new = head["ops"][(op, dimension)]["records"] if (op, dimension) in head["ops"] else 0
        rows.append({"op": op, "dimension": dimension, "base": old, "head": new,
                     "state": _state(old, new, (op, dimension) in changed)})
    return rows


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


def entry_status(entry, base, head, used):
    """Exercise counts of the keys a manifest entry names and its state; `why` for an unused entry."""
    change, call, pattern = entry.get("change"), entry.get("call"), entry.get("dimension")

    def named(dimension):
        return not pattern or fnmatch.fnmatchcase(dimension or "", pattern)

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
        counts = [(base["ops"][key]["records"] if key in base["ops"] else 0,
                   head["ops"][key]["records"] if key in head["ops"] else 0)
                  for key in set(base["ops"]) | set(head["ops"]) if key[0] == call and named(key[1])]
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
    else:
        state, why = "not-exercised", "claimed call not exercised on either side"
    return {**status, "state": state, "why": why}
