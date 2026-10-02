"""Collecting half of the differential run (differential_run.py): normalized records per tree.

Reads what LiteDB.Fuzz writes into each run directory (run.json, outcomes.jsonl, the
cleanup-obligation files, faults.jsonl, markers.json and, for teardown-faults,
teardown.jsonl) and aggregates it per tree. Nothing here compares two trees.

Every aggregate keeps its exercise count (records, evaluations or rows per key), so the
comparison can tell "exercised and unchanged" from "never exercised".

FaultDisposed rows (faults.jsonl) are keyed by teardown path (`op`), fault site and
injector model, because one path reaches many sites and the same declared -> observed
pair from one site must not stand in for another:

- site: the row's `site` field, else the fault of the fired FaultReached row of the same
  step written before it (the harness writes the two rows back to back);
- model: the row's `model` field, else the model (`fail-inside`, `skip`) of the
  teardown.jsonl case row with the same step, site and driver path; '-' when unknown.
"""
import json
from collections import Counter, defaultdict
from pathlib import Path

HARNESS_FILE = "outcomes.jsonl"
OBLIGATIONS = {"connection-clean.jsonl": "ConnectionClean", "quiescent.jsonl": "Quiescent",
               "scratch-live.jsonl": "ScratchLive", "closed-clean.jsonl": "ClosedClean"}  # last two: optional/legacy
OPTIONAL = {"payloadDigest": "payloads", "effectsDigest": "effects", "primaryExceptionType": "primary"}
UNKNOWN = "-"


def jsonl(path):
    if not path.is_file():
        return []
    lines = (line.strip() for line in path.read_text(encoding="utf-8", errors="replace").splitlines())
    return [json.loads(line) for line in lines if line]


def new_op():
    return {"outcomes": Counter(), "exceptions": Counter(), "primary": Counter(), "payloads": Counter(),
            "effects": Counter(), "permitted": set(), "unpermitted": Counter(), "records": 0,
            "present": Counter()}


def new_site():
    return {"pairs": Counter(), "rows": 0}


def exception_name(name, code):
    return name + (f"#{code}" if code is not None else "") if name else "none"


def new_summary():
    return {"ops": defaultdict(new_op), "obligations": defaultdict(lambda: {"evaluations": 0, "unclean": 0,
            "violations": Counter()}), "markers": Counter(), "faults": Counter(),
            "dispositions": defaultdict(lambda: defaultdict(new_site)), "runs": 0, "withOutcomes": 0}


def collect(roots, requested=None):
    """Aggregate normalized outcomes of every run below roots; requested = {(target, seed)} drops corpus replays."""
    summary = new_summary()
    for root in roots:
        for run_json in sorted(Path(root).rglob("run.json")):
            directory = run_json.parent
            meta = json.loads(run_json.read_text(encoding="utf-8"))
            if requested is not None and (meta.get("target"), meta.get("seed")) not in requested:
                continue
            summary["runs"] += 1
            summary["withOutcomes"] += (directory / HARNESS_FILE).is_file()
            for record in jsonl(directory / HARNESS_FILE):
                _add_outcome(summary["ops"][(record.get("op"), record.get("dimension") or "")], record)
            for name, oracle in OBLIGATIONS.items():
                for record in jsonl(directory / name):
                    key = (oracle, record.get("op") or record.get("point") or oracle, record.get("dimension") or "")
                    entry = summary["obligations"][key]
                    entry["evaluations"] += 1
                    entry["unclean"] += record.get("clean") is False
                    entry["violations"].update(_violation_kind(item) for item in record.get("violations") or [])
            _add_faults(summary, directory)
            markers = directory / "markers.json"
            if markers.is_file():
                summary["markers"].update(json.loads(markers.read_text(encoding="utf-8")).get("hits", {}))
    return summary


def _add_faults(summary, directory):
    """FaultReached rows count fired fault points; FaultDisposed rows count per (path, site, model)."""
    models = {(row.get("step"), row.get("site"), str(row.get("driver") or "").split("/")[0]): row.get("model")
              for row in jsonl(directory / "teardown.jsonl") if row.get("fired") and row.get("model") != "baseline"}
    reached = {}
    for record in jsonl(directory / "faults.jsonl"):
        if record.get("fault"):
            reached[record.get("step")] = record["fault"] if record.get("fired") else None
            if record.get("fired"):
                summary["faults"][record["fault"]] += 1
        elif record.get("op"):
            site = record.get("site") or reached.get(record.get("step"))
            model = record.get("model") or models.get((record.get("step"), site, record["op"]))
            entry = summary["dispositions"][record["op"]][(site or UNKNOWN, model or UNKNOWN)]
            entry["rows"] += 1
            entry["pairs"][f"{record.get('declared')} -> {record.get('observed')}"] += 1


def path_pairs(sites):
    """The declared -> observed pairs of one teardown path over all its sites."""
    return sum((entry["pairs"] for entry in sites.values()), Counter())


def site_dimension(site, model):
    """The dimension a FaultDisposed key reports under, so manifest patterns can name a site or model."""
    return f"site={site};model={model}"


def _add_outcome(entry, record):
    entry["records"] += 1
    entry["outcomes"][record.get("outcome")] += 1
    escaped = exception_name(record.get("exceptionType"), record.get("errorCode"))
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
    outcome, escaped = record.get("outcome"), exception_name(record.get("exceptionType"), record.get("errorCode"))
    return any(item in allowed for item in (outcome, f"{outcome}:{escaped.split('#')[0]}", f"{outcome}:{escaped}"))


def _violation_kind(item):
    text = item.get("kind") if isinstance(item, dict) else str(item)
    return str(text).split(":", 1)[0].strip() or "unspecified"
