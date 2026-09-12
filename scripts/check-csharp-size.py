"""Check changed C# files; read staged content for the pre-commit hook."""
import argparse
import json
import subprocess
from pathlib import Path


def git(*args):
    return subprocess.check_output(["git", *args]).decode("utf-8-sig")


parser = argparse.ArgumentParser()
parser.add_argument("--base")
args = parser.parse_args()
exceptions = json.loads(Path("scripts/csharp-size-exceptions.json").read_text())
diff = ["diff", "--name-only", "--diff-filter=ACMR", "-z"]
diff += [args.base, "HEAD"] if args.base else ["--cached"]
paths = git(*diff, "--", "*.cs").split("\0")
failed = False
for filename in filter(None, paths):
    if filename.endswith((".g.cs", ".generated.cs", ".Designer.cs")):
        continue
    source = git("show", ("HEAD:" if args.base else ":") + filename)
    lines = len(source.splitlines())
    exception = exceptions.get(filename)
    limit = exception["maxLines"] if exception else 500
    if lines > limit:
        print(f"ERROR: {filename}: {lines} lines exceeds {limit}; split by responsibility.")
        failed = True
    elif lines > 300:
        reason = f" ({exception['reason']})" if exception else ""
        print(f"WARNING: {filename}: {lines} lines exceeds the recommended 300{reason}")
raise SystemExit(1 if failed else 0)
