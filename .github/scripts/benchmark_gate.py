"""Evaluate hosted A/B raw rounds. Exit 0=pass, 1=fail/error, 2=noisy (rerun)."""
import argparse
import csv
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


def read_samples(directory, config, operating_system, framework):
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
    if os.environ.get("BENCHMARK_CANDIDATE_SHA") and commits != {os.environ["BENCHMARK_CANDIDATE_SHA"]}:
        raise ValueError("Measured candidate does not match the workflow checkout")
    return samples


def timing(values):
    return dict(medianMs=statistics.median(values), minMs=min(values), maxMs=max(values),
                spreadPercent=100 * (max(values) / min(values) - 1))


def evaluate(directory, config, operating_system, framework):
    if operating_system not in config["operatingSystems"] or framework not in config["frameworks"]:
        raise ValueError("Unexpected OS/runtime")
    samples = read_samples(directory, config, operating_system, framework)
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
    one_writer = timing(samples["dev-default", "durable_insert_5k_1_writers"])["medianMs"]
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
    return "\n".join(lines) + "\n"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    parser.add_argument("--config", type=Path, required=True)
    parser.add_argument("--os", required=True)
    parser.add_argument("--framework", required=True)
    args = parser.parse_args()
    try:
        report = evaluate(args.directory, json.loads(args.config.read_text()), args.os, args.framework)
    except (ValueError, KeyError, TypeError, OSError) as error:
        print(f"Benchmark evidence error: {error}", file=sys.stderr)
        return 1
    (args.directory / "gate.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    rows = [{key: value for key, value in row.items() if key not in ("baseline", "candidate")}
            for row in report["results"]]
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
    return {"pass": 0, "fail": 1, "noisy": 2}[report["verdict"]]


if __name__ == "__main__":
    sys.exit(main())
