"""Compare paired production runs, including consumed-query checksums."""
import argparse
import json
from pathlib import Path
from statistics import median


parser = argparse.ArgumentParser()
parser.add_argument("--before", nargs="+", required=True, type=Path)
parser.add_argument("--after", nargs="+", required=True, type=Path)
args = parser.parse_args()


def load(paths):
    cases = {}
    for path in paths:
        data = json.loads(path.read_text())
        for result in data["results"]:
            entry = cases.setdefault(result["name"], {
                "nanoseconds": [], "bytes": [], "gen0Per1000": [], "checksums": set()
            })
            for metric in ("nanoseconds", "bytes", "gen0Per1000"):
                entry[metric].extend(result[metric])
            entry["checksums"].add(result["checksum"])
    return cases


before, after = load(args.before), load(args.after)
print("| Workload | Before µs | After µs | Time reduction | Before B/op | After B/op | Allocation reduction |")
print("|---|---:|---:|---:|---:|---:|---:|")
for name, old in before.items():
    new = after[name]
    if len(old["checksums"]) != 1 or old["checksums"] != new["checksums"]:
        raise ValueError(f"Query result checksum mismatch: {name}")
    old_time, new_time = median(old["nanoseconds"]), median(new["nanoseconds"])
    old_bytes, new_bytes = median(old["bytes"]), median(new["bytes"])
    print(f"| {name} | {old_time / 1000:.2f} | {new_time / 1000:.2f} | "
          f"{(1 - new_time / old_time) * 100:.1f}% | {old_bytes:.0f} | {new_bytes:.0f} | "
          f"{(1 - new_bytes / old_bytes) * 100:.1f}% |")

print("\n| Reusable binding workload | µs/op | B/op | Gen0 / 1,000 ops |")
print("|---|---:|---:|---:|")
for name, result in after.items():
    if name not in before:
        print(f"| {name} | {median(result['nanoseconds']) / 1000:.2f} | "
              f"{median(result['bytes']):.0f} | {median(result['gen0Per1000']):.2f} |")
