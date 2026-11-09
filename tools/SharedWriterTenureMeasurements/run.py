"""Fork-CI only: identical production runners, serial alternating paired rounds."""
import json
import os
from pathlib import Path
import statistics
import subprocess
import sys
import time


def command(runner, *args):
    return ["dotnet", str(Path(runner).resolve() / "SharedWriterTenureMeasurements.dll"), *map(str, args)]


def run(runner, *args):
    return subprocess.check_output(command(runner, *args), text=True, timeout=90).strip()


def alternating(runner, path, durable, count=200):
    workers = [subprocess.Popen(command(runner, "worker", path, durable), stdin=subprocess.PIPE,
                               stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True) for _ in range(2)]
    try:
        for worker in workers:
            if worker.stdout.readline().strip() != "ready":
                raise RuntimeError("worker failed to attach: " + worker.stderr.read())
        latencies = []
        wall = time.monotonic()
        for value in range(1, count + 1):
            worker = workers[value % 2]
            worker.stdin.write(str(value) + "\n")
            worker.stdin.flush()
            result = worker.stdout.readline().strip()
            if not result:
                raise RuntimeError("worker failed: " + worker.stderr.read())
            latencies.append(float(result))
        seconds = time.monotonic() - wall
        for worker in workers:
            worker.stdin.write("close\n")
            worker.stdin.flush()
        for worker in workers:
            if worker.wait(timeout=20):
                raise RuntimeError(worker.stderr.read())
        run(runner, "verify", path, count)
        return {"mode": "alternating", "durable": durable == "true", "count": count, "seconds": seconds,
                "p50Microseconds": statistics.median(latencies),
                "p95Microseconds": sorted(latencies)[int(count * .95)]}
    finally:
        for worker in workers:
            if worker.poll() is None:
                worker.kill()
            worker.wait(timeout=20)


def main():
    if os.environ.get("GITHUB_REPOSITORY") != "JKamsker/LiteDB" or os.environ.get("CI") != "true":
        raise SystemExit("Measurements are fork-CI only")
    baseline, candidate, scratch, output = sys.argv[1:]
    scratch, output = Path(scratch).resolve(), Path(output)
    output.mkdir(parents=True, exist_ok=True)
    records = []
    for round_number in range(5):
        variants = [("before", baseline), ("after", candidate)]
        if round_number % 2:
            variants.reverse()
        for durable in ("false", "true"):
            for mode in ("single", "alternating"):
                for variant, runner in variants:
                    path = scratch / f"{round_number}-{durable}-{mode}-{variant}" / "test.db"
                    run(runner, "seed", path)
                    if mode == "single":
                        count = 2000 if durable == "false" else 500
                        result = json.loads(run(runner, "single", path, durable, count))
                        run(runner, "verify", path, count)
                    else:
                        result = alternating(runner, path, durable)
                    records.append(dict(result, variant=variant, round=round_number))
                    (output / "paired.json").write_text(json.dumps(records, indent=2) + "\n")
    summary = []
    for durable in (False, True):
        for mode, metric in (("single", "transactionsPerSecond"), ("alternating", "p50Microseconds"), ("alternating", "p95Microseconds")):
            pairs = []
            for round_number in range(5):
                sample = {r["variant"]: r[metric] for r in records if r["mode"] == mode and r["durable"] == durable and r["round"] == round_number}
                pairs.append(sample["after"] / sample["before"])
            summary.append(dict(mode=mode, durable=durable, metric=metric, afterBeforeMedian=statistics.median(pairs), ratios=pairs))
    (output / "summary.json").write_text(json.dumps(summary, indent=2) + "\n")
    print(json.dumps(summary, indent=2))


if __name__ == "__main__":
    main()
