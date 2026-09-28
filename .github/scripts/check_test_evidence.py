"""Safety evidence: a green CI run must be complete, current and actually executed.

Runs after every build/test job of _reusable-ci.yml. It fails when a required job
did not succeed (skipped, cancelled, failed), when an expected test leg uploaded
no evidence or an unexpected one did, when a leg tested another revision, when a
partition result is missing, empty, failed or aborted, when a discovered test
produced no result, and when a test ran on no leg at all without a quarantine
entry. It checks that results exist and executed; it does not judge prose.
"""
import argparse
import itertools
import json
import os
import sys
import xml.etree.ElementTree as ElementTree
from pathlib import Path

import safety_common as common

CONFIG = f"{common.SAFETY_DIR}/ci-evidence.json"
LEDGER = f"{common.SAFETY_DIR}/coverage-ledger.json"
TRX = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"
EXECUTED = {"Passed", "Failed", "Pass", "Fail", "Error", "Timeout"}
FAILED_COUNTERS = ("failed", "error", "timeout", "aborted")
SUITE_ROOT = "LiteDB.Tests/"
RUNTIME_GUARD = "LiteDB.Tests.Engine.TestHost_Tests.RequestedRuntimeAndArchitecture_AreActuallyRunning"


def check_jobs(needs, config, tier, report):
    jobs = config.get("jobs", {})
    for name, job in sorted(jobs.items()):
        expected = "success" if tier in job.get("tiers", []) else "skipped"
        actual = needs.get(name, {}).get("result")
        if name not in needs:
            report.error(f"Job {name} is declared in {CONFIG} but is not a dependency of the evidence job")
        elif actual != expected:
            report.error(f"Job {name} finished as '{actual}', but the {tier} tier requires '{expected}'")
    for name in sorted(set(needs) - set(jobs)):
        report.error(f"Job {name} is not declared in {CONFIG}; declare the tiers it must succeed in")


def check_leg_set(legs, config, tier, report):
    for name, job in sorted(config.get("jobs", {}).items()):
        spec = job.get("legs", {}).get(tier)
        actual = [_project(leg["matrix"], spec) for leg in legs if leg.get("job") == name] if spec else []
        if not spec:
            if any(leg.get("job") == name for leg in legs):
                report.error(f"Job {name} uploaded leg evidence although the {tier} tier expects none")
            continue
        expected = [tuple(json.dumps(value) for value in combo) for combo in itertools.product(*spec.values())]
        for combo in sorted(set(expected) - set(actual)):
            report.error(f"Job {name}: no evidence for the expected leg {_describe(spec, combo)}")
        for combo in sorted(set(actual) - set(expected)):
            report.error(f"Job {name}: unexpected leg {_describe(spec, combo)}; declare it in {CONFIG}")
        for combo in sorted({combo for combo in actual if actual.count(combo) > 1}):
            report.error(f"Job {name}: leg {_describe(spec, combo)} uploaded evidence more than once")


def _project(matrix, spec):
    values = []
    for key in spec:
        value = matrix or {}
        for part in key.split("."):
            value = value.get(part) if isinstance(value, dict) else None
        values.append(json.dumps(value))
    return tuple(values)


def _describe(spec, combo):
    return ", ".join(f"{key}={json.loads(value)}" for key, value in zip(spec, combo))


def read_trx(path):
    root = ElementTree.parse(path).getroot()
    methods = {}
    for unit in root.iter(f"{TRX}UnitTest"):
        method = unit.find(f"{TRX}TestMethod")
        if method is not None:
            methods[unit.get("id")] = f"{method.get('className')}.{method.get('name')}"
    results = [(methods.get(result.get("testId"), result.get("testName")), result.get("outcome"))
               for result in root.iter(f"{TRX}UnitTestResult")]
    summary = root.find(f"{TRX}ResultSummary")
    counters = {} if summary is None or summary.find(f"{TRX}Counters") is None else {
        key: int(value) for key, value in summary.find(f"{TRX}Counters").attrib.items() if value.isdigit()}
    return results, counters, None if summary is None else summary.get("outcome")


def read_xunit(path):
    root = ElementTree.parse(path).getroot()
    results = [(f"{test.get('type')}.{test.get('method')}", test.get("result")) for test in root.iter("test")]
    counters = {"total": 0, "failed": 0, "error": 0}
    for assembly in root.iter("assembly"):
        counters["total"] += int(assembly.get("total", 0))
        counters["failed"] += int(assembly.get("failed", 0))
        counters["error"] += int(assembly.get("errors", 0))
    return results, counters, "Completed"


def check_leg(leg, directory, sha, report, outcomes):
    label = f"{leg.get('job')} {json.dumps(leg.get('matrix'), sort_keys=True)}"
    if leg.get("sha") != sha:
        report.error(f"{label} tested {leg.get('sha')}, not the validated revision {sha}")
    if leg.get("buildSha") != sha:  # a missing build record is not proof of the candidate's binaries
        report.error(f"{label} ran binaries built from {leg.get('buildSha') or 'an unrecorded revision'}, not {sha}")
    reported, summary = _check_partitions(leg, directory, label, report, outcomes)
    if "Passed" not in reported.get(RUNTIME_GUARD, set()) and "Pass" not in reported.get(RUNTIME_GUARD, set()):
        report.error(f"{label}: the runtime/architecture guard {RUNTIME_GUARD} did not pass on this leg")
    discovery = leg.get("discovery")
    if discovery:
        path = directory / discovery
        if not path.is_file():
            report.error(f"{label}: discovery listing {discovery} is missing")
        else:
            listed = {line.strip() for line in path.read_text(encoding="utf-8-sig").splitlines() if line.strip()}
            missing = sorted(listed - set(reported))
            if missing:
                report.error(f"{label}: {len(missing)} discovered tests produced no result "
                             f"(first: {', '.join(missing[:5])})")
            for fqn in listed:
                outcomes.setdefault(fqn, set())
    return {"job": leg.get("job"), "matrix": leg.get("matrix"), "sha": leg.get("sha"),
            "runtime": leg.get("runtimeDescription"), "partitions": summary}


def _check_partitions(leg, directory, label, report, outcomes):
    """Parse every declared partition result plus any extra TRX; return ({fqn: outcomes}, counters)."""
    reader = read_xunit if leg.get("format") == "xunit" else read_trx
    files = dict(leg.get("partitions") or {})
    files.update({f"extra:{path.name}": path.name for path in directory.glob("*.trx")
                  if path.name not in files.values()})
    reported, summary = {}, {}
    for partition, name in sorted(files.items()):
        path = directory / name
        if not path.is_file():
            report.error(f"{label}: partition {partition} has no result file {name}")
            continue
        try:
            results, counters, outcome = reader(path)
        except ElementTree.ParseError as error:
            report.error(f"{label}: partition {partition} result {name} is not valid XML (truncated?): {error}")
            continue
        summary[partition] = counters
        bad = {key: counters.get(key, 0) for key in FAILED_COUNTERS if counters.get(key, 0)}
        if not partition.startswith("extra:") and (counters.get("total", 0) == 0 or not results):
            report.error(f"{label}: partition {partition} reported no tests")
        if bad or outcome in ("Aborted", "Timeout", "Error"):
            report.error(f"{label}: partition {partition} did not pass cleanly ({outcome}, {bad})")
        for fqn, result in results:
            reported.setdefault(fqn, set()).add(result)
            outcomes.setdefault(fqn, set()).add(result)
    return reported, summary


def check_execution(outcomes, source_tests, quarantine, report, shown=20):
    quarantined = {entry["test"]: entry for entry in quarantine}
    never = sorted(fqn for fqn in set(outcomes) | set(source_tests)
                   if not (outcomes.get(fqn, set()) & EXECUTED))
    unexplained = [fqn for fqn in never if fqn not in quarantined]
    for fqn in unexplained[:shown]:
        if outcomes.get(fqn):
            state = "was skipped on every leg"
        elif fqn in outcomes:
            state = "was discovered but produced no result on any leg"
        else:
            state = "was not discovered on any leg"
        report.error(f"{fqn} {state}; fix it or quarantine it in {LEDGER} with an owner and review date")
    if len(unexplained) > shown:
        report.error(f"{len(unexplained) - shown} more tests executed on no leg (see the errors above for the cause)")
    for fqn in sorted(set(quarantined) - set(never)):
        report.warning(f"Quarantined test {fqn} executed on a leg; remove its quarantine entry")
    return [fqn for fqn in never if fqn in quarantined]


def load_legs(root, report):
    legs = []
    for path in sorted(Path(root).rglob("evidence-leg.json")):
        try:
            leg = json.loads(path.read_text(encoding="utf-8-sig"))
        except ValueError as error:
            report.error(f"{path.parent.name}: evidence-leg.json is not valid JSON: {error}")
            continue
        if isinstance(leg, dict):
            legs.append((leg, path.parent))
        else:
            report.error(f"{path.parent.name}: evidence-leg.json must be a JSON object")
    return legs


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--artifacts", required=True, help="Directory holding the downloaded test-results-* artifacts")
    parser.add_argument("--tier", required=True, choices=["pr", "full"])
    parser.add_argument("--sha", required=True, help="The revision this run validates (github.sha)")
    parser.add_argument("--needs-env", default="NEEDS_JSON", help="Environment variable holding toJSON(needs)")
    parser.add_argument("--head", default="HEAD")
    parser.add_argument("--config", help=f"Use this file instead of {CONFIG} (local runs of a subset of legs)")
    parser.add_argument("--output", help="Directory for safety-evidence.json")
    args = parser.parse_args(argv)
    head = common.Tree(args.head)
    report = common.Report("Safety evidence")
    try:
        config = common.load_json_file(args.config) if args.config else head.read_json(CONFIG, {})
        ledger = head.read_json(LEDGER, {})
    except (common.MalformedJson, ValueError) as error:
        report.error(str(error))
        config, ledger = {}, {}
    jobs = common.section(config or {}, "jobs", dict, report, CONFIG)
    config = {"jobs": {name: job for name, job in jobs.items() if isinstance(job, dict)}}
    check_jobs(json.loads(os.environ.get(args.needs_env) or "{}"), config, args.tier, report)
    legs = load_legs(args.artifacts, report)
    check_leg_set([leg for leg, _ in legs], config, args.tier, report)
    outcomes, summaries = {}, []
    for leg, directory in legs:
        summaries.append(check_leg(leg, directory, args.sha, report, outcomes))
    source = {fqn for fqn, (path, _) in common.collect_tests(head).items() if path.startswith(SUITE_ROOT)}
    quarantine = [entry for entry in common.section(ledger or {}, "quarantine", list, report, LEDGER) if entry.get("test")]
    quarantined = check_execution(outcomes, source, quarantine, report)
    attempt = int(os.environ.get("GITHUB_RUN_ATTEMPT", "1"))
    if attempt > 1:
        report.warning(f"This is run attempt {attempt}; classify the failures of earlier attempts in the PR")
    rows = ["| Job | Leg | Partitions | Tests | Failed |", "| --- | --- | --- | --- | --- |"]
    for item in summaries:
        total = sum(counter.get("total", 0) for counter in item["partitions"].values())
        failed = sum(counter.get(key, 0) for counter in item["partitions"].values() for key in FAILED_COUNTERS)
        rows.append(f"| {item['job']} | `{json.dumps(item['matrix'], sort_keys=True)}` | "
                    f"{len(item['partitions'])} | {total} | {failed} |")
    report.section(f"Validated revision `{args.sha}` ({args.tier} tier, attempt {attempt}), {len(legs)} legs.\n\n"
                   + "\n".join(rows))
    report.section(f"Quarantined, never executed (visible coverage gaps): {len(quarantined)}"
                   + "".join(f"\n- `{fqn}`" for fqn in quarantined))
    if args.output:
        Path(args.output).mkdir(parents=True, exist_ok=True)
        Path(args.output, "safety-evidence.json").write_text(json.dumps({
            "sha": args.sha, "tier": args.tier, "attempt": attempt, "legs": summaries,
            "quarantinedNeverExecuted": quarantined, "errors": report.errors, "warnings": report.warnings},
            indent=2), encoding="utf-8")
    return report.finish()


if __name__ == "__main__":
    sys.exit(main())
