"""Report only; process startup, warmup, seeding and verification are untimed."""
import datetime
import json
import os
from pathlib import Path
import shutil
import statistics
import subprocess
import sys
import tempfile
import time


def sample(runner, scenario, workers, output):
    root = Path(tempfile.mkdtemp(prefix="litedb-request-"))
    command = ["dotnet", str(Path(runner).resolve()), "seed", str(root), scenario, str(workers), str(200 if scenario == "read" else 20)]
    children = []
    try:
        subprocess.run(command, check=True, timeout=120)
        for worker in range(workers):
            args = command.copy()
            args[2] = "worker"
            children.append(subprocess.Popen(args + [str(worker)], stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True))
        deadline = time.monotonic() + 120
        while not all((root / f"ready-{i}").exists() for i in range(workers)):
            if time.monotonic() > deadline or any(p.poll() is not None for p in children):
                raise RuntimeError("worker failed before barrier")
            time.sleep(.005)
        deadline = time.monotonic() + 600
        start = time.perf_counter()
        release = datetime.datetime.now(datetime.timezone.utc) + datetime.timedelta(milliseconds=250)
        (root / "release").write_text(release.isoformat())
        (root / "release").rename(root / "go")
        while not all((root / f"done-{i}.json").exists() for i in range(workers)):
            if time.monotonic() > deadline or any(p.poll() not in (None, 0) for p in children):
                raise RuntimeError("worker failed in measurement or exceeded 600 seconds")
            time.sleep(.001)
        elapsed = (time.perf_counter() - start) * 1000
        for child in children:
            stdout, stderr = child.communicate(timeout=30)
            if child.returncode:
                raise RuntimeError(stdout + stderr)
        rows = [json.loads((root / f"done-{i}.json").read_text()) for i in range(workers)]
        events = sorted([(r['start'], 1) for r in rows] + [(r['end'], -1) for r in rows])
        active = peak = 0
        for _, delta in events:
            active += delta
            peak = max(peak, active)
        if workers > 1 and peak < 2:
            raise RuntimeError("no measured process overlap")
        command[2] = "verify"
        subprocess.run(command, check=True, timeout=120)
        result = dict(scenario=scenario, workers=workers, requestsPerWorker=int(command[-1]), peakOverlappingWorkers=peak, warmupRequests=10,
                      latencyMs=statistics.mean(r["milliseconds"] / int(command[-1]) for r in rows),
                      wallMsPerRequest=max(0, elapsed - 250) / (int(command[-1]) * workers), processes=rows, verified=True)
        output.write_text(json.dumps(result, indent=2) + "\n")
        return result
    finally:
        for child in children:
            if child.poll() is None:
                child.kill()
            stdout, stderr = child.communicate()
            if stderr:
                print(stderr, file=sys.stderr)
        shutil.rmtree(root)


def main():
    if os.environ.get("GITHUB_ACTIONS") != "true":
        raise RuntimeError("No local benchmarks")
    v5, v6, directory = sys.argv[1:]
    output = Path(directory)
    output.mkdir(parents=True, exist_ok=True)
    rows = []
    for repetition in (1, 2):
        for round_id in range(6):
            order = [("v5", v5), ("v6", v6)]
            if (round_id + repetition) % 2:
                order.reverse()
            for scenario in ("read", "insert", "mixed"):
                for workers in (1, 4):
                    for variant, runner in order:
                        name = f"rep{repetition}-round{round_id}-{scenario}-{workers}-{variant}.json"
                        row = sample(runner, scenario, workers, output / name)
                        row.update(repetition=repetition, round=round_id, variant=variant)
                        (output / name).write_text(json.dumps(row, indent=2) + "\n")
                        rows.append(row)
                        print(f"{name}: {row['latencyMs']:.4f} ms/request", flush=True)
    (output / "all.json").write_text(json.dumps(rows, indent=2) + "\n")


if __name__ == "__main__":
    main()
