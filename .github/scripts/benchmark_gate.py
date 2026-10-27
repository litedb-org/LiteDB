"""Evaluate hosted A/B raw rounds. Exit 0=pass, 1=fail/error, 2=noisy (rerun)."""
import argparse
import csv
import copy
import json
import math
import os
from pathlib import Path
import statistics
import sys


def positive(value):
    return (isinstance(value, (int, float)) and not isinstance(value, bool)
            and math.isfinite(value) and value > 0)


def validate_config(config):
    if config["schemaVersion"] != 1 or any(
            type(n) is not int or n < 3
            for n in [config["rounds"], *config.get("roundsByOS", {}).values()]):
        raise ValueError("Unsupported schema or insufficient rounds")
    if not positive(config["maxSpreadPercent"]):
        raise ValueError("Invalid noise limit")
    configs = [config["baseline"], config["candidate"], *config["reportOnlyConfigs"]]
    if len(set(configs)) != 3 or config["reportOnlyConfigs"] != ["dev-default"]:
        raise ValueError("Expected three distinct comparison configurations")
    for rule in config["scenarios"].values():
        if not positive(rule["operations"]):
            raise ValueError("Invalid operation count")
        if rule["maxRatio"] is not None and not positive(rule["maxRatio"]):
            raise ValueError("Invalid ratio threshold")


def rounds_for_os(config, operating_system):
    return config.get("roundsByOS", {}).get(operating_system, config["rounds"])


def read_samples(directory, config, operating_system, framework, expected_commit=None):
    """Reject incomplete/duplicate rounds and workload changes before judging speed."""
    validate_config(config)
    configs = [config["baseline"], config["candidate"], *config["reportOnlyConfigs"]]
    seen = set()
    samples = {}
    identities = {name: set() for name in configs}
    for path in sorted(directory.glob("round-*.json")):
        result = json.loads(path.read_text(encoding="utf-8"))
        if result["runnerOS"] != operating_system or result["framework"] != f".NETCoreApp,Version=v{framework[3:]}":
            raise ValueError(f"Round OS/runtime mismatch: {path.name}")
        if not result["runtime"].startswith(f".NET {framework[3:]}."):
            raise ValueError(f"Actual runtime differs from target: {path.name}")
        name, number = result["config"], result["round"]
        if name not in configs or type(number) is not int or not 1 <= number <= rounds_for_os(config, operating_system):
            raise ValueError(f"Unexpected round: {path.name}")
        if (name, number) in seen:
            raise ValueError(f"Duplicate round: {name}/{number}")
        seen.add((name, number))
        digest = result["assemblySha256"]
        if not isinstance(digest, str) or len(digest) != 64 or any(char not in "0123456789abcdefABCDEF" for char in digest):
            raise ValueError(f"Invalid binary identity: {path.name}")
        identities[name].add((result["assemblySha256"], result["candidateCommit"]))
        expected = dict(config["scenarios"])
        if name == "dev-default":
            expected.update(config["scalingScenarios"])
        scenarios = [sample["scenario"] for sample in result["samples"]]
        if len(scenarios) != len(expected) or set(scenarios) != set(expected):
            raise ValueError(f"Missing, duplicate or unexpected scenarios: {path.name}")
        for sample in result["samples"]:
            scenario, elapsed = sample["scenario"], sample["milliseconds"]
            if (not positive(elapsed) or type(sample["operations"]) is not int
                    or sample["operations"] != expected[scenario]["operations"]):
                raise ValueError(f"Invalid time or workload size: {path.name}/{scenario}")
            samples.setdefault((name, scenario), []).append(elapsed)
    if len(seen) != len(configs) * rounds_for_os(config, operating_system):
        raise ValueError("Incomplete round set; do not judge partial measurements")
    if any(len(identity) != 1 for identity in identities.values()):
        raise ValueError("Binary or candidate changed between rounds")
    if identities[config["candidate"]] != identities["dev-default"]:
        raise ValueError("v6 configurations did not load the same binary/commit")
    commits = {commit for identity in identities.values() for _, commit in identity}
    if len(commits) != 1 or not next(iter(commits)):
        raise ValueError("Missing or inconsistent candidate commit")
    expected_commit = expected_commit or os.environ.get("BENCHMARK_CANDIDATE_SHA")
    if expected_commit and commits != {expected_commit}:
        raise ValueError("Measured candidate does not match the workflow checkout")
    return samples


def timing(values):
    return dict(medianMs=statistics.median(values), minMs=min(values), maxMs=max(values),
                spreadPercent=100 * (max(values) / min(values) - 1))


def evaluate(directory, config, operating_system, framework, expected_commit=None):
    if operating_system not in config["operatingSystems"] or framework not in config["frameworks"]:
        raise ValueError("Unexpected OS/runtime")
    samples = read_samples(directory, config, operating_system, framework, expected_commit)
    results = []
    for scenario, rule in config["scenarios"].items():
        baseline = timing(samples[config["baseline"], scenario])
        for name in [config["candidate"], *config["reportOnlyConfigs"]]:
            candidate = timing(samples[name, scenario])
            ratio = candidate["medianMs"] / baseline["medianMs"]
            lower = candidate["minMs"] / baseline["maxMs"]
            upper = candidate["maxMs"] / baseline["minMs"]
            limit = rule["maxRatio"] if name == config["candidate"] else None
            verdict, reason = "report-only", "Outside the Phase 1 gate"
            if limit is not None:
                if lower > limit:
                    verdict, reason = "fail", "min(B)/max(A) exceeds threshold despite spread"
                elif ratio <= limit and upper <= limit:
                    verdict, reason = "pass", "max(B)/min(A) meets threshold despite spread"
                elif max(baseline["spreadPercent"], candidate["spreadPercent"]) > config["maxSpreadPercent"]:
                    verdict, reason = "noisy", "Observed ratio bounds straddle threshold and spread exceeds limit; rerun this OS/runtime"
                elif ratio > limit:
                    verdict, reason = "fail", "Median B/A exceeds threshold"
                else:
                    verdict, reason = "pass", "Median B/A meets threshold"
            results.append(dict(os=operating_system, framework=framework, scenario=scenario,
                                config=name, verdict=verdict, reason=reason, ratio=ratio,
                                ratioLowerBound=lower, ratioUpperBound=upper,
                                maxRatio=limit, baseline=baseline, candidate=candidate))
    one_writer = (timing(samples["dev-default", "durable_insert_5k_1_writers"])["medianMs"]
                  if config["scalingScenarios"] else None)
    for scenario in config["scalingScenarios"]:
        candidate = timing(samples["dev-default", scenario])
        results.append(dict(os=operating_system, framework=framework, scenario=scenario,
                            config="dev-default", verdict="report-only",
                            reason="v6 durable scaling only; v5 has no commit device-sync setting",
                            ratio=None, ratioLowerBound=None, ratioUpperBound=None, maxRatio=None, baseline=None, candidate=candidate,
                            speedupVsOneWriter=one_writer / candidate["medianMs"]))
    verdicts = {row["verdict"] for row in results}
    verdict = "fail" if "fail" in verdicts else "noisy" if "noisy" in verdicts else "pass"
    return dict(schemaVersion=1, os=operating_system, framework=framework, verdict=verdict,
                rounds=rounds_for_os(config, operating_system), maxSpreadPercent=config["maxSpreadPercent"],
                rerunRequired="noisy" in verdicts, results=results)


def evaluate_comparison(directory, config, operating_system, framework, baseline_sha, scenarios=""):
    """Full comparisons retain gate rules; subsets never issue a gate verdict."""
    if not baseline_sha:
        raise ValueError("Comparison requires a baseline SHA")
    if scenarios:
        names = scenarios.split(",")
        if len(names) != len(set(names)) or not set(names) <= set(config["scenarios"]):
            raise ValueError("Unknown or duplicate comparison scenarios")
        config = copy.deepcopy(config)
        config["scenarios"] = {name: dict(config["scenarios"][name], maxRatio=None) for name in names}
        config["scalingScenarios"] = {}
    after = evaluate(directory, config, operating_system, framework)
    before = evaluate(directory / "before", config, operating_system, framework, baseline_sha)

    def shared_v5(folder):
        rounds = {}
        for path in folder.glob("round-*.json"):
            result = json.loads(path.read_text(encoding="utf-8"))
            if result["config"] == config["baseline"]:
                result.pop("candidateCommit")
                rounds[result["round"]] = result
        return rounds

    if shared_v5(directory) != shared_v5(directory / "before"):
        raise ValueError("Before and after must share identical v5 measurements")
    after["baselineCommit"] = baseline_sha
    after["baselineGate"] = before
    after["comparisonOnly"] = bool(scenarios)
    after["comparisons"] = []
    for a, b in zip(before["results"], after["results"]):
        old, new = a["candidate"], b["candidate"]
        after["comparisons"].append(dict(
            os=operating_system, framework=framework, scenario=b["scenario"], config=b["config"],
            verdict="report-only", reason="Same-job after/before; lower is faster",
            ratio=new["medianMs"] / old["medianMs"],
            ratioLowerBound=new["minMs"] / old["maxMs"],
            ratioUpperBound=new["maxMs"] / old["minMs"], maxRatio=None,
            baseline=old, candidate=new))
    if scenarios:
        after["verdict"] = before["verdict"] = "report-only"
    return after


def image_warnings(current, reference):
    """Advisory only: historical environments may use the original runner fields."""
    def identity(metadata):
        runner = metadata.get("runner", {})
        return (metadata.get("imageName", runner.get("ImageOS")),
                metadata.get("imageVersion", runner.get("ImageVersion")))
    after, before = identity(current), identity(reference)
    if all(after) and all(before) and after != before:
        return [f"WARNING: Comparing differing runner images: {before[0]} / {before[1]} "
                f"vs {after[0]} / {after[1]}. Cross-image timings may shift; verdict unchanged."]
    return []


def markdown(report):
    lines = [f"## Phase 1 benchmark gate: {report['verdict'].upper()} — {report['os']} / {report['framework']}",
             "", "B/A uses medians. Durable default, checkpoint, open/close and v6 writer scaling are report-only.",
             "Bounds are observed min(B)/max(A) to max(B)/min(A), not confidence intervals.",
             "", "| Scenario | Config | B/A | Lower bound | Upper bound | Limit | Verdict |",
             "|---|---|---:|---:|---:|---:|---|"]
    for row in report["results"]:
        ratio = f"{row['ratio']:.3f}" if row["ratio"] is not None else "v6 scaling only"
        limit = str(row["maxRatio"]) if row["maxRatio"] is not None else "—"
        lower = f"{row['ratioLowerBound']:.3f}" if row["ratioLowerBound"] is not None else "—"
        upper = f"{row['ratioUpperBound']:.3f}" if row["ratioUpperBound"] is not None else "—"
        lines.append(f"| {row['scenario']} | {row['config']} | {ratio} | {lower} | {upper} | {limit} | {row['verdict']} |")
    if report["rerunRequired"]:
        lines += ["", "NOISY: rerun this OS/runtime before drawing conclusions for noisy scenarios.",
                  "Stable failures remain failures; noisy scenarios are inconclusive, never passes."]
    if "baselineGate" in report:
        lines += ["", "### Before revision vs v5", markdown(report["baselineGate"]),
                  "### Same-job after/before (report-only; lower is faster)", "",
                  "| Scenario | Config | After/before | Lower bound | Upper bound |",
                  "|---|---|---:|---:|---:|"]
        for row in report["comparisons"]:
            lines.append(f"| {row['scenario']} | {row['config']} | {row['ratio']:.3f} | "
                         f"{row['ratioLowerBound']:.3f} | {row['ratioUpperBound']:.3f} |")
        if report["comparisonOnly"]:
            lines += ["", "Comparison subset only: no Phase 1 gate verdict is issued."]
    lines += ["", *report.get("warnings", [])]
    return "\n".join(lines) + "\n"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    parser.add_argument("--config", type=Path, required=True)
    parser.add_argument("--os", required=True)
    parser.add_argument("--framework", required=True)
    parser.add_argument("--baseline-sha", default="")
    parser.add_argument("--scenarios", default="")
    parser.add_argument("--compare-environment", type=Path,
                        help="Historical environment.json to compare (warning only)")
    args = parser.parse_args()
    try:
        config = json.loads(args.config.read_text())
        if args.scenarios and not args.baseline_sha:
            raise ValueError("Scenario subsets require comparison mode")
        report = (evaluate_comparison(args.directory, config, args.os, args.framework,
                                      args.baseline_sha, args.scenarios) if args.baseline_sha else
                  evaluate(args.directory, config, args.os, args.framework))
        reference = args.compare_environment or (args.directory / "before" / "environment.json")
        current = args.directory / "environment.json"
        if reference.is_file() and current.is_file():
            report["warnings"] = image_warnings(json.loads(current.read_text(encoding="utf-8")),
                                                json.loads(reference.read_text(encoding="utf-8")))
    except (ValueError, KeyError, TypeError, OSError) as error:
        print(f"Benchmark evidence error: {error}", file=sys.stderr)
        return 1
    (args.directory / "gate.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    rows = [{key: value for key, value in row.items() if key not in ("baseline", "candidate")}
            for row in report["results"]]
    if "baselineGate" in report:
        rows = [dict(comparison="after/v5", **row) for row in rows]
        for label, source in (("before/v5", report["baselineGate"]["results"]),
                              ("after/before", report["comparisons"])):
            rows.extend(dict(comparison=label, **{k: v for k, v in row.items()
                        if k not in ("baseline", "candidate")}) for row in source)
    with (args.directory / "gate.csv").open("w", newline="", encoding="utf-8") as handle:
        writer = csv.DictWriter(handle, fieldnames=list(dict.fromkeys(key for row in rows for key in row)))
        writer.writeheader()
        writer.writerows(rows)
    text = markdown(report)
    (args.directory / "gate.md").write_text(text, encoding="utf-8")
    print(text)
    if os.environ.get("GITHUB_STEP_SUMMARY"):
        with open(os.environ["GITHUB_STEP_SUMMARY"], "a", encoding="utf-8") as handle:
            handle.write(text)
    if os.environ.get("GITHUB_OUTPUT"):
        with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as handle:
            handle.write(f"verdict={report['verdict']}\nrerun-required={str(report['rerunRequired']).lower()}\n")
    return {"pass": 0, "fail": 1, "noisy": 2, "report-only": 0}[report["verdict"]]


if __name__ == "__main__":
    sys.exit(main())
