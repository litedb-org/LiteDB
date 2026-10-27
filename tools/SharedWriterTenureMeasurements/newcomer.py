"""Linux x64 fork-CI only: fresh attachments to an outgoing mapped writer."""
import json
import math
import os
from pathlib import Path
import platform
import select
import statistics
import subprocess
import sys

from run import command, run


class Worker:
    def __init__(self, runner, path, durable, errors):
        self.closed = False
        self.errors = errors.open("w")
        self.process = subprocess.Popen(command(runner, "newcomer", path, durable),
                                        stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                        stderr=self.errors, text=True)

    def read(self):
        if not select.select([self.process.stdout], [], [], 30)[0]:
            raise TimeoutError("Newcomer handshake stalled; see worker stderr")
        line = self.process.stdout.readline().strip()
        if not line:
            raise RuntimeError("Newcomer worker exited; see worker stderr")
        return line

    def exchange(self, line):
        self.process.stdin.write(line + "\n")
        self.process.stdin.flush()
        return self.read()

    def close(self):
        if self.closed:
            return
        try:
            if self.process.poll() is None:
                self.process.stdin.write("close\n")
                self.process.stdin.flush()
            if self.process.wait(timeout=15):
                raise RuntimeError("Newcomer worker failed; see stderr")
        finally:
            if self.process.poll() is None:
                self.process.kill()
                self.process.wait(timeout=15)
            self.errors.close()
            self.closed = True


def percentile(samples, quantile):
    return sorted(samples)[math.ceil(len(samples) * quantile) - 1]


def measure(runner, path, durable, output, count=200, warmup=10):
    workers = []
    try:
        for slot in range(2):
            worker = Worker(runner, path, durable, output / f"worker-{slot}.stderr")
            workers.append(worker)
            if worker.read() != "ready":
                raise RuntimeError("Bad worker readiness")
        json.loads(workers[0].exchange("acquire 0"))
        prepared = json.loads(workers[0].exchange("prepare 0"))
        samples = []
        for turn in range(1, warmup + count + 1):
            incoming, outgoing = workers[turn % 2], workers[(turn - 1) % 2]
            acquired = json.loads(incoming.exchange(f"acquire {turn}"))
            if outgoing.exchange("detach") != "detached":
                raise RuntimeError("Outgoing peer failed to detach")
            if turn > warmup:
                samples.append(dict(acquired, turn=turn, worker=turn % 2, outgoing=prepared))
                (output / "samples.json").write_text(json.dumps(samples, indent=2) + "\n")
            prepared = json.loads(incoming.exchange(f"prepare {turn}"))
        # Close both peers before independently checking documents/index/sentinel.
        for worker in workers:
            worker.close()
        workers.clear()
        run(runner, "verify", path, warmup + count)
        latencies = [sample["attachToAcquireMicroseconds"] for sample in samples]
        return dict(mode="newcomer", durable=durable == "true", count=count, warmup=warmup,
                    p50Microseconds=percentile(latencies, .50),
                    p99Microseconds=percentile(latencies, .99), samples=samples)
    finally:
        cleanup_error = None
        for worker in workers:
            try:
                worker.close()
            except Exception as error:
                cleanup_error = error
        if cleanup_error is not None:
            raise cleanup_error


def main():
    if (os.environ.get("CI") != "true" or os.environ.get("GITHUB_REPOSITORY") != "JKamsker/LiteDB"
            or platform.system() != "Linux" or platform.machine() != "x86_64"):
        raise SystemExit("Newcomer measurements are Linux x64 fork-CI only")
    baseline, candidate, scratch, destination = sys.argv[1:]
    output = Path(destination).resolve()
    output.mkdir(parents=True, exist_ok=True)
    records = []
    for round_number in range(5):
        variants = [("before", baseline), ("after", candidate)]
        if round_number % 2:
            variants.reverse()
        for durable in ("false", "true"):
            for variant, runner in variants:
                sample_dir = output / f"{round_number}-{durable}-{variant}"
                sample_dir.mkdir()
                path = Path(scratch).resolve() / sample_dir.name / "test.db"
                run(runner, "seed", path)
                result = measure(runner, path, durable, sample_dir)
                records.append(dict(result, variant=variant, round=round_number))
                (output / "newcomer-raw.json").write_text(json.dumps(records, indent=2) + "\n")
    summary = []
    for durable in (False, True):
        for metric in ("p50Microseconds", "p99Microseconds"):
            pairs = []
            for round_number in range(5):
                sample = {r["variant"]: r[metric] for r in records
                          if r["durable"] == durable and r["round"] == round_number}
                pairs.append(sample["after"] / sample["before"])
            summary.append(dict(durable=durable, metric=metric,
                                afterBeforeMedian=statistics.median(pairs), ratios=pairs))
    (output / "newcomer-summary.json").write_text(json.dumps(summary, indent=2) + "\n")
    print(json.dumps(summary, indent=2))


if __name__ == "__main__":
    main()
