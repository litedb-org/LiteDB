"""Fork-CI-only, serial alternating 50-cycle mapped open/close comparisons."""
import json
import os
from pathlib import Path
import statistics
import subprocess
import sys


def main():
    if os.environ.get("GITHUB_ACTIONS") != "true" or os.environ.get("GITHUB_REPOSITORY") != "JKamsker/LiteDB":
        raise RuntimeError("Timing runs only in fork GitHub CI")
    baseline, candidate, scratch, output, base_sha, head_sha = sys.argv[1:]
    output = Path(output)
    output.mkdir(parents=True, exist_ok=True)
    rows = []
    with (output / "raw.jsonl").open("w") as stream:
        for pair in range(5):
            order = [("baseline", baseline, base_sha), ("candidate", candidate, head_sha)]
            if pair % 2:
                order.reverse()
            for variant, runner, sha in order:
                result = subprocess.run(["dotnet", str(Path(runner) / "SharedOpenCloseMeasurements.dll"),
                                         scratch, variant, sha], text=True, capture_output=True, check=True, timeout=120)
                row = json.loads(result.stdout)
                if row["cycles"] != 50 or row["sourceSha"] != sha or row["perCycleMs"] <= 0:
                    raise RuntimeError("Incomplete measurement")
                row["pair"] = pair
                rows.append(row)
                stream.write(json.dumps(row) + "\n")
                stream.flush()
                print(json.dumps(row), flush=True)
    summary = {variant: statistics.median(row["perCycleMs"] for row in rows if row["variant"] == variant)
               for variant in ("baseline", "candidate")}
    (output / "summary.json").write_text(json.dumps(summary, indent=2) + "\n")
    report = (f"Mapped one-file open/close, 50 cycles per sample, five alternating pairs; "
              f"median ms/cycle: before `{base_sha}` **{summary['baseline']:.3f}**, "
              f"after `{head_sha}` **{summary['candidate']:.3f}**.\n")
    (output / "summary.md").write_text(report)
    with open(os.environ["GITHUB_STEP_SUMMARY"], "a") as stream:
        stream.write(report)


if __name__ == "__main__":
    main()
