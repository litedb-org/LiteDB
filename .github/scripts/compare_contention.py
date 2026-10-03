"""Contended writer acquisition: metrics and a base-vs-head tolerance gate.

Input is the acquire JSONL written by scripts/measure-shared-contention.py (its
contended-acquire part, in the same paired rounds as the writer contention): one record per run,
each with `variant` (baseline|candidate), `processes`, `thinkUs`, the Stopwatch
`frequency` and raw `samples` rows [worker, seq, ticket, arrival, acquired, released]
in ticks of one host-wide monotonic clock.

Definitions (per run):
- acquire latency = acquired - arrival. `arrival` is stamped immediately before
  BeginTrans; `acquired` immediately after it returns, i.e. after the named writer
  mutex is owned, the engine is open and the transaction has started. Latency thus
  includes a per-build constant for engine open; acquisition ORDER is exact because
  every `acquired` stamp lies inside its exclusive ownership interval.
- p50/p99 use the nearest-rank method; max wait is the largest latency.
- overtaking: an acquisition e overtakes when some other waiter w arrived earlier
  (arrival_w + grace < arrival_e) and was still waiting when e acquired
  (acquired_w > acquired_e): a later arrival won while an earlier one waited. This
  is overtaking, not starvation (an overtaken waiter may still progress). Each
  acquisition counts at most once; `overtakes` counts every (e, w) pair.
  Rate = overtaking count / acquisitions.
  `grace` (default 0.1 ms) absorbs the gap between the arrival stamp and the native
  request, so near-simultaneous arrivals are not ordered by clock noise.
- hand-off gap: for each acquisition (in ownership order) whose waiter arrived before
  the previous owner's release stamp, acquired - that release stamp (clamped at 0).
  It excludes the predecessor's hold time (a durable commit), so it isolates wake-up
  plus engine open: how long the mutex sat free while someone was waiting.
- waiter age: at each acquisition, the age (now - arrival) of the oldest OTHER
  process still waiting; reported as p99 and max over acquisitions.
- waiter progress, per process: acquisition count and share (fixed by the iteration
  count, so a check, not a measure), the longest interval between its successive
  acquisitions, its longest wait, and `othersWhileWaiting`: the most acquisitions by
  other processes during one of its waits. A FIFO queue of P processes allows at most
  P - 1; the run-level value is the maximum over processes.

Gate (per process count and think time, over all rounds of each side):
- compare the MEDIAN across rounds of each per-run metric, never one run. Rounds
  alternate base/head order (the driver does this) so host drift hits both sides.
- p99 fails only if head > base x 1.5 AND head > base + 5 ms. Why: shared CI runners
  share disks and CPUs with other tenants; each acquisition waits for a peer's durable
  commit, so latency is dominated by fsync time that varies 2x between runs, and a
  p99 over a few hundred samples rests on its ~4 largest values. The relative factor
  ignores proportional jitter on slow disks, the absolute floor ignores sub-ms jitter
  when the base is fast. It catches tail shifts comparable to the commit time (a
  lost wake-up rescued by a long timeout, serialized engine opens); a short poll
  period hides in this tail and is the hand-off gate's job.
- overtaking rate fails if it rises by more than 5 percentage points. Why: with a
  fair hand-off the rate is near zero and moves by a few points between identical
  runs (0% at two processes; 1-6% per run at four, whose turnstile is not FIFO among
  three waiters); a releaser that barges past a queued waiter wins a large share.
- othersWhileWaiting fails only if head > base x 2 AND head > base + 3. Why: it is
  a count, not a time, so disk and scheduler noise barely move it: identical builds
  gave exactly 1 at two processes in every run and medians of 5-7 at four; a waiter
  bypassed by a barging owner sees hundreds of acquisitions during one wait.
- median hand-off fails only if head > base x 2 AND head > base + 1 ms. Why: the
  median gap is a sub-millisecond, fsync-free quantity that stays within ~0.1 ms
  between identical builds even under host load, while a waiter that polls instead of
  blocking adds about half its poll period to every contended hand-off. The factor
  and floor keep a slower engine open (part of the gap) from failing on its own.
- max wait fails only if head > base x 3 AND head > base + 100 ms. Why: one run's max
  is a single sample (a stalled fsync on a shared runner adds tens of ms), but its
  median across rounds is stable; a waiter starved by a barging owner waits for
  that owner's whole run of transactions (seconds), while p99 can even FALL then,
  because the barging owner's own re-acquisitions are instant and dominate the samples.
- reported, not gated: acquire p50 (tracks the peer's commit time), hand-off p99
  (host scheduling noise), waiter age p99/max (bounded by max wait, which is gated)
  and the longest progress interval (a peer's hold plus one wait; max wait covers it).
Performance is evidence class 3: repeated paired measurements against stated
tolerances, never exact equality. The net's mode comes from .github/safety/net-modes.json
("contended-acquire"): in advisory mode findings are reported and the exit code is 0.
Unusable input (unverified runs, too few rounds, nothing comparable) always fails.
All thresholds are flags; a PR that loosens them must say why.
"""
import argparse
import bisect
import json
import math
import statistics
import sys
from collections import defaultdict

import safety_common as common

FIELDS = ("worker", "seq", "ticket", "arrival", "acquired", "released")
WORKER, SEQ, TICKET, ARRIVAL, ACQUIRED, RELEASED = range(len(FIELDS))
NET = "contended-acquire"
DEFAULTS = {"p99_factor": 1.5, "p99_floor_ms": 5.0, "overtaking_pp": 5.0, "handoff_factor": 2.0,
            "handoff_floor_ms": 1.0, "max_factor": 3.0, "max_floor_ms": 100.0, "others_factor": 2.0,
            "others_floor": 3.0, "grace_ms": 0.1, "min_rounds": 5}


def percentile(values, fraction):
    """Nearest-rank percentile: the smallest value with at least `fraction` of samples at or below it."""
    if not values:
        raise ValueError("percentile of no samples")
    ordered = sorted(values)
    rank = math.ceil(round(fraction * len(ordered), 9))  # round: 0.29 * 100 must be rank 29, not 30
    return ordered[min(max(rank, 1), len(ordered)) - 1]


def overtaking(samples, grace_ticks=0):
    """(overtaking acquisitions, overtaken waiter pairs) for the sample rows."""
    by_arrival = sorted(samples, key=lambda row: row[ARRIVAL])
    arrivals = [row[ARRIVAL] for row in by_arrival]
    ranks = {value: index + 1 for index, value in enumerate(sorted({row[ACQUIRED] for row in samples}))}
    tree = [0] * (len(ranks) + 1)  # Fenwick tree over acquisition ranks of earlier arrivals

    def add(index):
        while index < len(tree):
            tree[index] += 1
            index += index & -index

    def at_most(index):
        total = 0
        while index > 0:
            total += tree[index]
            index -= index & -index
        return total

    inserted = count = overtakes = 0
    for row in by_arrival:
        # Earlier arrivals: strictly more than `grace` before this one.
        limit = bisect.bisect_left(arrivals, row[ARRIVAL] - grace_ticks)
        while inserted < limit:
            add(ranks[by_arrival[inserted][ACQUIRED]])
            inserted += 1
        later = inserted - at_most(ranks[row[ACQUIRED]])  # earlier arrivals acquiring after this one
        overtakes += later
        count += later > 0
    return count, overtakes


def handoffs(samples):
    """Contended hand-off gaps in ticks: acquired - previous owner's release stamp, for
    every acquisition (in ownership order) whose waiter arrived before that release.
    The release stamp follows Commit's return, so it can trail the true release; a
    negative gap therefore means an immediate hand-off and is clamped to zero."""
    ordered = sorted(samples, key=lambda row: row[ACQUIRED])
    return [max(0, row[ACQUIRED] - previous[RELEASED]) for previous, row in zip(ordered, ordered[1:])
            if row[ARRIVAL] < previous[RELEASED]]


def _by_worker(samples):
    rows = defaultdict(list)
    for row in samples:
        rows[row[WORKER]].append(row)
    for own in rows.values():
        own.sort(key=lambda row: row[SEQ])
    return rows


def waiter_ages(samples):
    """For each acquisition, ticks the oldest other still-waiting process has waited (0 if none)."""
    rows = _by_worker(samples)
    arrivals = {worker: [row[ARRIVAL] for row in own] for worker, own in rows.items()}
    ages = []
    for row in samples:
        now, oldest = row[ACQUIRED], 0
        for worker, own in rows.items():
            index = bisect.bisect_left(arrivals[worker], now) - 1  # its latest arrival before now
            if worker != row[WORKER] and index >= 0 and own[index][ACQUIRED] > now:
                oldest = max(oldest, now - own[index][ARRIVAL])
        ages.append(oldest)
    return ages


def progress(samples, frequency):
    """Per-process progress: count, share, longest acquisition interval, longest wait and
    the most acquisitions by others during one of its waits."""
    to_ms = 1000.0 / frequency
    acquired = sorted(row[ACQUIRED] for row in samples)
    result = {}
    for worker, own in sorted(_by_worker(samples).items()):
        intervals = [(row[ACQUIRED] - previous[ACQUIRED]) * to_ms for previous, row in zip(own, own[1:])]
        # Acquisitions strictly inside (arrival, acquired) belong to other processes: one waiter per process.
        others = [bisect.bisect_left(acquired, row[ACQUIRED]) - bisect.bisect_right(acquired, row[ARRIVAL])
                  for row in own]
        result[str(worker)] = {
            "count": len(own), "share": len(own) / len(samples), "longestIntervalMs": max(intervals, default=0.0),
            "longestWaitMs": max((row[ACQUIRED] - row[ARRIVAL]) * to_ms for row in own),
            "othersWhileWaiting": max(others)}
    return result


def metrics(samples, frequency, grace_ms=DEFAULTS["grace_ms"]):
    """Per-run acquisition metrics in milliseconds."""
    to_ms = 1000.0 / frequency
    waits = [(row[ACQUIRED] - row[ARRIVAL]) * to_ms for row in samples]
    holds = [(row[RELEASED] - row[ACQUIRED]) * to_ms for row in samples]
    gaps = [gap * to_ms for gap in handoffs(samples)] or [0.0]
    ages = [age * to_ms for age in waiter_ages(samples)]
    per_process = progress(samples, frequency)
    count, overtakes = overtaking(samples, grace_ticks=grace_ms * frequency / 1000.0)
    return {
        "acquisitions": len(samples), "perProcess": per_process,
        "p50Ms": percentile(waits, .50), "p99Ms": percentile(waits, .99), "maxWaitMs": max(waits),
        "meanMs": statistics.fmean(waits), "holdP50Ms": percentile(holds, .50),
        "handoffs": len(gaps), "handoffP50Ms": percentile(gaps, .50), "handoffP99Ms": percentile(gaps, .99),
        "overtakingCount": count, "overtakingRate": count / len(samples), "overtakes": overtakes,
        "graceMs": grace_ms, "oldestWaiterAgeP99Ms": percentile(ages, .99), "oldestWaiterAgeMaxMs": max(ages),
        "longestIntervalMs": max(item["longestIntervalMs"] for item in per_process.values()),
        "othersWhileWaiting": max(item["othersWhileWaiting"] for item in per_process.values()),
    }


def check_samples(samples, frequency, processes, iterations, bracket_ns=None):
    """Problems that make a run unusable as evidence (empty when consistent)."""
    problems = []
    rows = _by_worker(samples)
    if sorted(rows) != list(range(processes)):
        problems.append(f"workers {sorted(rows)} != 0..{processes - 1}")
    for worker, own in sorted(rows.items()):
        if [row[SEQ] for row in own] != list(range(iterations)):
            problems.append(f"worker {worker}: sequence is not 0..{iterations - 1}")
        for previous, row in zip([None] + own, own):
            if not row[ARRIVAL] <= row[ACQUIRED] <= row[RELEASED]:
                problems.append(f"worker {worker} seq {row[SEQ]}: stamps out of order")
            if previous is not None and previous[RELEASED] > row[ARRIVAL]:
                problems.append(f"worker {worker} seq {row[SEQ]}: arrived before its previous release")
    tickets = sorted(row[TICKET] for row in samples)
    if tickets and tickets != list(range(tickets[0], tickets[0] + len(tickets))):
        problems.append("tickets are not one contiguous run: lost or duplicated writer ownership")
    by_acquired = [row[TICKET] for row in sorted(samples, key=lambda row: row[ACQUIRED])]
    if by_acquired != sorted(by_acquired):
        problems.append("acquisition-stamp order differs from ticket order: clocks are not comparable "
                        "across processes or ownership was not exclusive")
    if bracket_ns:
        low, high = bracket_ns
        outside = [row for row in samples
                   if not all(low <= row[index] * 1e9 / frequency <= high for index in (ARRIVAL, RELEASED))]
        if outside:
            problems.append(f"{len(outside)} samples outside the driver's clock bracket: the worker clock is "
                            "not the host-wide monotonic clock")
    return problems


def summarize_side(runs):
    """Median across rounds of every gated/reported per-run metric."""
    keys = ("p50Ms", "p99Ms", "maxWaitMs", "overtakingRate", "overtakingCount", "handoffP50Ms", "handoffP99Ms",
            "oldestWaiterAgeP99Ms", "oldestWaiterAgeMaxMs", "longestIntervalMs", "othersWhileWaiting")
    return {key: statistics.median(run[key] for run in runs) for key in keys}


def exceeds(base, head, factor, floor):
    return head > base * factor and head > base + floor


def decide(base, head, options):
    """Failure reasons for one scenario, given median metrics of each side."""
    reasons = []
    if exceeds(base["maxWaitMs"], head["maxWaitMs"], options.max_factor, options.max_floor_ms):
        reasons.append(f"max wait {head['maxWaitMs']:.2f} ms > base {base['maxWaitMs']:.2f} ms "
                       f"x {options.max_factor} and + {options.max_floor_ms} ms")
    if exceeds(base["handoffP50Ms"], head["handoffP50Ms"], options.handoff_factor, options.handoff_floor_ms):
        reasons.append(f"median hand-off {head['handoffP50Ms']:.2f} ms > base {base['handoffP50Ms']:.2f} ms "
                       f"x {options.handoff_factor} and + {options.handoff_floor_ms} ms")
    if exceeds(base["p99Ms"], head["p99Ms"], options.p99_factor, options.p99_floor_ms):
        reasons.append(f"p99 acquire {head['p99Ms']:.2f} ms > base {base['p99Ms']:.2f} ms x {options.p99_factor} "
                       f"and + {options.p99_floor_ms} ms")
    rise = (head["overtakingRate"] - base["overtakingRate"]) * 100
    if rise > options.overtaking_pp:
        reasons.append(f"overtaking rate rose {rise:.1f} pp (> {options.overtaking_pp} pp): "
                       f"{base['overtakingRate']:.1%} -> {head['overtakingRate']:.1%}")
    if exceeds(base["othersWhileWaiting"], head["othersWhileWaiting"], options.others_factor, options.others_floor):
        reasons.append(f"a waiter saw {head['othersWhileWaiting']:g} other acquisitions during one wait > base "
                       f"{base['othersWhileWaiting']:g} x {options.others_factor} and + {options.others_floor:g}")
    return reasons


def compare(base_runs, head_runs, report, options):
    """Gate each (processes, thinkUs) scenario; returns (rows for the summary table, unusable-input errors)."""
    grouped = defaultdict(lambda: ([], []))
    for side, runs in ((0, base_runs), (1, head_runs)):
        for run in runs:
            grouped[(run["processes"], run.get("thinkUs", 0))][side].append(
                metrics(run["samples"], run["frequency"], options.grace_ms))
    rows, unusable = [], []
    for (processes, think), (base, head) in sorted(grouped.items()):
        label = f"{processes} processes, think {think} us"
        if len(base) < options.min_rounds or len(head) < options.min_rounds:
            unusable.append(f"{label}: {len(base)} base / {len(head)} head rounds; {options.min_rounds} required")
            continue
        base_median, head_median = summarize_side(base), summarize_side(head)
        reasons = decide(base_median, head_median, options)
        for reason in reasons:
            report.error(f"{label}: {reason}")
        rows.append((label, len(base), len(head), base_median, head_median, "fail" if reasons else "pass"))
    if not rows and not unusable:
        unusable.append("No comparable runs: both sides need results for the same scenarios")
    for error in unusable:
        report.error(error)
    return rows, unusable


def table(rows):
    lines = ["| Scenario | Rounds b/h | p50 ms b/h | p99 ms b/h | max ms b/h | overtaking b/h "
             "| hand-off p50/p99 ms b/h | oldest waiter p99/max ms b/h | others while waiting b/h "
             "| longest interval ms b/h | Gate |", "|" + " --- |" * 11]
    for label, nb, nh, base, head, status in rows:
        def pair(key, form=".2f"):
            return f"{base[key]:{form}}/{head[key]:{form}}"
        lines.append(f"| {label} | {nb}/{nh} | {pair('p50Ms')} | {pair('p99Ms')} | {pair('maxWaitMs')} | "
                     f"{pair('overtakingRate', '.1%')} | {pair('handoffP50Ms')}, {pair('handoffP99Ms')} | "
                     f"{pair('oldestWaiterAgeP99Ms')}, {pair('oldestWaiterAgeMaxMs')} | "
                     f"{pair('othersWhileWaiting', 'g')} | {pair('longestIntervalMs', '.1f')} | {status} |")
    return "\n".join(lines)


def load(paths):
    runs = []
    for path in paths:
        with open(path, encoding="utf-8") as handle:
            runs += [json.loads(line) for line in handle if line.strip()]
    failed = [run for run in runs if not run.get("passed")]
    if failed:
        raise ValueError(f"{len(failed)} unverified run(s) in {', '.join(map(str, paths))}")
    return runs


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--results", nargs="+", default=[], help="Paired driver output; `variant` selects the side")
    parser.add_argument("--base", nargs="+", default=[], help="Runs of the base build (any variant label)")
    parser.add_argument("--head", nargs="+", default=[], help="Runs of the head build (any variant label)")
    parser.add_argument("--p99-factor", type=float, default=DEFAULTS["p99_factor"])
    parser.add_argument("--p99-floor-ms", type=float, default=DEFAULTS["p99_floor_ms"])
    parser.add_argument("--overtaking-pp", type=float, default=DEFAULTS["overtaking_pp"])
    parser.add_argument("--others-factor", type=float, default=DEFAULTS["others_factor"])
    parser.add_argument("--others-floor", type=float, default=DEFAULTS["others_floor"])
    parser.add_argument("--handoff-factor", type=float, default=DEFAULTS["handoff_factor"])
    parser.add_argument("--handoff-floor-ms", type=float, default=DEFAULTS["handoff_floor_ms"])
    parser.add_argument("--max-factor", type=float, default=DEFAULTS["max_factor"])
    parser.add_argument("--max-floor-ms", type=float, default=DEFAULTS["max_floor_ms"])
    parser.add_argument("--grace-ms", type=float, default=DEFAULTS["grace_ms"])
    parser.add_argument("--min-rounds", type=int, default=DEFAULTS["min_rounds"])
    common.add_mode_arguments(parser)
    args = parser.parse_args(argv)
    if bool(args.results) == bool(args.base or args.head) or bool(args.base) != bool(args.head):
        parser.error("use either --results or both --base and --head")
    report = common.Report("Contended writer acquisition")
    try:
        if args.results:
            runs = load(args.results)
            base = [run for run in runs if run["variant"] == "baseline"]
            head = [run for run in runs if run["variant"] == "candidate"]
        else:
            base, head = load(args.base), load(args.head)
        rows, unusable = compare(base, head, report, args)
    except (OSError, ValueError, KeyError) as error:
        report.error(f"Unreadable results: {error}")
        rows, unusable = [], [error]
    if rows:
        report.section(table(rows))
    report.section(f"Tolerance: p99 fails above base x {args.p99_factor} and base + {args.p99_floor_ms} ms; "
                   f"overtaking fails above +{args.overtaking_pp} pp; others-while-waiting fails above base x "
                   f"{args.others_factor} and base + {args.others_floor:g}; median hand-off fails above base x "
                   f"{args.handoff_factor} and base + {args.handoff_floor_ms} ms; max wait fails above base x "
                   f"{args.max_factor} and base + {args.max_floor_ms} ms; medians across rounds; "
                   f"arrival grace {args.grace_ms} ms.")
    # Unusable input is a broken measurement, not a finding: it fails in every mode.
    return report.finish(advisory=not unusable and common.net_advisory(NET, args.blocking))


if __name__ == "__main__":
    sys.exit(main())
