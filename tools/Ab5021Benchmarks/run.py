"""Run only on hosted CI. One optimized, warmed child per version/configuration/round."""
import csv
import hashlib
import json
import os
from pathlib import Path
import platform
import statistics
import subprocess
import sys
import time


ROOT = Path(__file__).resolve().parent
CONFIGS = ["v5", "dev-default", "dev-os-cache"]
GATE_CONFIG = json.loads((ROOT / "gate.json").read_text())
ROUNDS = GATE_CONFIG["rounds"]


def rounds_for_os(operating_system):
    return GATE_CONFIG.get("roundsByOS", {}).get(operating_system, ROUNDS)


def execution_order(round_number):
    return CONFIGS if round_number % 2 else list(reversed(CONFIGS))


def comparison_order(round_number):
    variants = ["v5", "before", "after"]
    offset = (round_number - 1) % len(variants)
    variants = variants[offset:] + variants[:offset]
    configs = CONFIGS[1:] if round_number % 2 else list(reversed(CONFIGS[1:]))
    return [(variant, config) for variant in variants
            for config in (["v5"] if variant == "v5" else configs)]


def summarize(output):
    rounds = rounds_for_os(os.environ.get("RUNNER_OS"))
    raw = []
    for path in sorted(output.glob("round-*.json")):
        result = json.loads(path.read_text())
        for sample in result["samples"]:
            raw.append(dict(config=result["config"], round=result["round"], **sample))
    with (output / "samples.csv").open("w", newline="") as handle:
        writer = csv.DictWriter(handle, fieldnames=list(raw[0]))
        writer.writeheader()
        writer.writerows(raw)
    summary = []
    for scenario in dict.fromkeys(row["scenario"] for row in raw):
        for config in (["dev-default"] if scenario.startswith("durable_insert_") else CONFIGS):
            values = [row["milliseconds"] for row in raw if row["scenario"] == scenario and row["config"] == config]
            if len(values) != rounds:
                raise RuntimeError(f"Expected {rounds} rounds: {scenario} {config}: {values}")
            scaling = scenario.startswith("durable_insert_")
            baseline = [row["milliseconds"] for row in raw if row["scenario"] == scenario and row["config"] == "v5"]
            one_writer = [row["milliseconds"] for row in raw if row["scenario"] == "durable_insert_5k_1_writers"
                          and row["config"] == "dev-default"]
            summary.append(dict(scenario=scenario, config=config, medianMs=statistics.median(values),
                                minMs=min(values), maxMs=max(values),
                                spreadPercent=(max(values) / min(values) - 1) * 100,
                                ratio=None if scaling else statistics.median(values) / statistics.median(baseline),
                                speedupVsOneWriter=statistics.median(one_writer) / statistics.median(values) if scaling else None))
    (output / "summary.json").write_text(json.dumps(summary, indent=2))
    with (output / "summary.csv").open("w", newline="") as handle:
        writer = csv.DictWriter(handle, fieldnames=list(summary[0]))
        writer.writeheader()
        writer.writerows(summary)
    title = f"{os.environ.get('RUNNER_OS')} / {sys.argv[1]}"
    lines = [f"## LiteDB 5.0.21 vs candidate {os.environ.get('BENCHMARK_CANDIDATE_SHA')} — {title}", "",
             f"Milliseconds; {rounds} rounds per configuration. B/A = dev / v5 (lower is faster).",
             "Direct, unencrypted local temp files; ordinal invariant collation; CHECKPOINT=1000 except explicit checkpoint=0 setup.",
             "TieredCompilation=0; identical shared source; Release / TestingEnabled=false.",
             "dev-os-cache uses DurableCommits=false; checkpoint/file-creation durability still differs from v5.", "",
             "| Scenario | v5 median / min | dev default median / min | B/A | dev OS cache median / min | B/A |",
             "|---|---:|---:|---:|---:|---:|"]
    for scenario in dict.fromkeys(row["scenario"] for row in summary if not row["scenario"].startswith("durable_insert_")):
        rows = [next(row for row in summary if row["scenario"] == scenario and row["config"] == config) for config in CONFIGS]
        a, b, c = rows
        timing = lambda r: f"{r['medianMs']:.2f} / {r['minMs']:.2f}"
        lines.append(f"| {scenario} | {timing(a)} | {timing(b)} | {b['ratio']:.3f} | {timing(c)} | {c['ratio']:.3f} |")
    lines += ["", "### Noise: round max/min minus one", "", "| Scenario | v5 | dev default | dev OS cache |", "|---|---:|---:|---:|"]
    for scenario in dict.fromkeys(row["scenario"] for row in summary if not row["scenario"].startswith("durable_insert_")):
        values = [next(row["spreadPercent"] for row in summary if row["scenario"] == scenario and row["config"] == config) for config in CONFIGS]
        lines.append(f"| {scenario} | " + " | ".join(f"{v:.1f}%" for v in values) + " |")
    lines += ["", "### Durable multi-thread inserts — v6 scaling only", "",
              "v5 has no per-commit device-sync setting; no v5 durable ratio is claimed.",
              "Same 5k auto-commit inserts, shared Direct database/collection, default CHECKPOINT=1000.",
              "Thread startup excluded; start barrier, scheduling, inserts and joins included.", "",
              "| Scenario | Median / min (ms) | Inserts/s | Speedup vs 1 writer | Spread |",
              "|---|---:|---:|---:|---:|"]
    for row in summary:
        if row["scenario"].startswith("durable_insert_"):
            lines.append(f"| {row['scenario']} | {row['medianMs']:.2f} / {row['minMs']:.2f} | "
                         f"{5000000 / row['medianMs']:.1f} | {row['speedupVsOneWriter']:.3f} | {row['spreadPercent']:.1f}% |")
    markdown = "\n".join(lines) + "\n"
    (output / "summary.md").write_text(markdown, encoding="utf-8")
    with open(os.environ["GITHUB_STEP_SUMMARY"], "a", encoding="utf-8") as handle:
        handle.write(markdown)
    print(markdown, flush=True)


def main():
    if os.environ.get("GITHUB_ACTIONS") != "true":
        raise RuntimeError("Measurements are restricted to GitHub Actions; do not run locally")
    framework, destination = sys.argv[1:]
    comparison = bool(os.environ.get("BENCHMARK_BASELINE_SHA"))
    scenarios = os.environ.get("BENCHMARK_SCENARIOS", "")
    if scenarios and (not comparison or len(set(scenarios.split(","))) != len(scenarios.split(","))
                      or not set(scenarios.split(",")) <= set(GATE_CONFIG["scenarios"])):
        raise ValueError("Scenario subsets require comparison mode and known, distinct scenarios")
    output = Path(destination).resolve()
    output.mkdir(parents=True, exist_ok=True)
    env = dict(os.environ, DOTNET_TieredCompilation="0", DOTNET_CLI_TELEMETRY_OPTOUT="1")
    metadata = dict(platform=platform.platform(), machine=platform.machine(), processor=platform.processor(),
                    kernel=platform.release(), imageName=os.environ.get("ImageOS", "unknown"),
                    imageVersion=os.environ.get("ImageVersion", "unknown"),
                    framework=framework, runner={key: value for key, value in os.environ.items()
                    if key in ["RUNNER_OS", "RUNNER_ARCH", "RUNNER_NAME", "ImageOS", "ImageVersion", "GITHUB_SHA"]},
                    candidateCommit=os.environ["BENCHMARK_CANDIDATE_SHA"],
                    harnessCommit=os.environ["BENCHMARK_HARNESS_SHA"],
                    sourceHashes={p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in ROOT.glob("*.cs")},
                    dotnetInfo=subprocess.check_output(["dotnet", "--info"], text=True),
                    order=[], started=time.time())
    if comparison:
        metadata.update(baselineCommit=os.environ["BENCHMARK_BASELINE_SHA"], comparisonScenarios=scenarios)
    (output / "environment.json").write_text(json.dumps(metadata, indent=2), encoding="utf-8")
    environment_summary = (f"### Runner environment — {framework}\n\n"
                           f"Image: {metadata['imageName']} / {metadata['imageVersion']}\n\n"
                           f"Kernel: {metadata['kernel']} ({platform.system()})\n\n")
    print(environment_summary, flush=True)
    with open(os.environ["GITHUB_STEP_SUMMARY"], "a", encoding="utf-8") as handle:
        handle.write(environment_summary)
    start = time.monotonic()
    rounds = rounds_for_os(os.environ["RUNNER_OS"])
    budget = 23 * 60 if comparison else 24 * 60 if os.environ["RUNNER_OS"] == "macOS" else 35 * 60
    for round_number in range(1, rounds + 1):
        # Filtered to either A/B pair, this gives A,B,B,A,A,B,B,A,...
        order = comparison_order(round_number) if comparison else [("after", c) for c in execution_order(round_number)]
        for variant, config in order:
            remaining = budget - (time.monotonic() - start)
            if remaining <= 0:
                raise TimeoutError("Measurement budget exceeded")
            project = "V5" if config == "v5" else "V6"
            dll = ROOT / project / "bin" / "Release" / framework / f"{project}.dll"
            if variant == "before":
                dll = ROOT / "V6Before" / f"{project}.dll"
            child_env = dict(env)
            if variant == "before":
                child_env["BENCHMARK_CANDIDATE_SHA"] = env["BENCHMARK_BASELINE_SHA"]
            child_output = output / "before" if variant == "before" else output
            child_output.mkdir(parents=True, exist_ok=True)
            result_path = child_output / f"round-{round_number}-{config}.json"
            command = ["dotnet", str(dll), config, str(round_number), str(result_path)]
            label = f"{variant}/{config}" if comparison else config
            print(f"Round {round_number}: {label} ({framework})", flush=True)
            identity = dict(round=round_number, config=config)
            if comparison:
                identity["variant"] = variant
            try:
                child = subprocess.run(command, env=child_env, text=True, stdout=subprocess.PIPE,
                                       stderr=subprocess.STDOUT, timeout=min(360, remaining))
            except subprocess.TimeoutExpired as error:
                partial = error.stdout or b""
                if isinstance(partial, bytes):
                    partial = partial.decode(errors="replace")
                (child_output / f"round-{round_number}-{config}.log").write_text(partial, encoding="utf-8")
                metadata["order"].append(dict(identity, error="child timeout"))
                (output / "environment.json").write_text(json.dumps(metadata, indent=2), encoding="utf-8")
                raise
            (child_output / f"round-{round_number}-{config}.log").write_text(child.stdout, encoding="utf-8")
            print(child.stdout, flush=True)
            metadata["order"].append(dict(identity, exitCode=child.returncode))
            (output / "environment.json").write_text(json.dumps(metadata, indent=2))
            child.check_returncode()
            if comparison and variant == "v5":
                # v5 is measured once per round and shared by the two vs-v5 evaluations.
                shared = json.loads(result_path.read_text())
                shared["candidateCommit"] = env["BENCHMARK_BASELINE_SHA"]
                before_output = output / "before"
                before_output.mkdir(exist_ok=True)
                (before_output / result_path.name).write_text(json.dumps(shared, indent=2))
    if not comparison:
        summarize(output)


if __name__ == "__main__":
    main()
