#!/usr/bin/env python3
"""Exercise the actual parity script with controlled runner failures, without compiling AOT."""

import os
from pathlib import Path
import shutil
import subprocess
import tempfile


def check_retry(outcome):
    with tempfile.TemporaryDirectory(prefix="litedb-aot-gate-") as temporary:
        root = Path(temporary)
        (root / "scripts").mkdir()
        (root / "LiteDB.AotTestHost").mkdir()
        (root / "LiteDB.AotTestHost/known-aot-differences.tsv").write_text("reflection\tUnrelated.Known.Test\n")
        script = root / "scripts/validate-aot-test-suite.sh"
        shutil.copyfile(Path(__file__).with_name(script.name), script)
        tools = root / "tools"
        tools.mkdir()
        fake_dotnet = tools / "dotnet"
        fake_dotnet.write_text('''#!/usr/bin/env bash
set -eu
while [ "$1" != --output ]; do shift; done
mkdir -p "$2"
cp "$GATE_FIXTURE" "$2/LiteDB.Tests"
chmod +x "$2/LiteDB.Tests"
''')
        fake_dotnet.chmod(0o755)
        runner = root / "runner"
        runner.write_text('''#!/usr/bin/env bash
set -eu
name=LiteDB.Tests.Engine.Regression
case "$1" in
    */regular.tsv)
        if [ "$RETRY_OUTCOME" = baseline_fail ]; then
            printf 'Fail\t%s\tBaseline fixture missing\n' "$name" > "$1"
        else
            printf 'Pass\t%s\t\n' "$name" > "$1"
        fi ;;
    */native-aot.tsv)
        detail=""
        case "$RETRY_OUTCOME" in
            platform | platform_windows)
                detail="PlatformNotSupportedException: Shared and coordinated connections are not supported under Native AOT on Unix because named mutexes do not synchronize across processes. Use a direct connection with exclusive process ownership." ;;
            unrelated_platform) detail="PlatformNotSupportedException: Something else is unsupported." ;;
        esac
        printf 'Fail\t%s\t%s\n' "$name" "$detail" > "$1" ;;
    */retry.tsv)
        case "$RETRY_OUTCOME" in
            crash) exit 137 ;;
            missing) exit 0 ;;
            pass_then_crash) printf 'Pass\t%s\t\n' "$name" > "$1"; exit 1 ;;
            pass) printf 'Pass\t%s\t\n' "$name" > "$1" ;;
            fail) printf 'Fail\t%s\t\n' "$name" > "$1" ;;
            skip) printf 'Skip\t%s\t\n' "$name" > "$1" ;;
        esac ;;
esac
''')
        environment = dict(os.environ, PATH=str(tools) + os.pathsep + os.environ["PATH"],
                           GATE_FIXTURE=str(runner), RETRY_OUTCOME=outcome,
                           TARGET_FRAMEWORK="net8.0", RUNTIME_IDENTIFIER="win-x64" if outcome == "platform_windows" else "linux-x64")
        result = subprocess.run(["bash", str(script)], env=environment, text=True,
                                stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=30)
        expected = 0 if outcome in ("pass", "baseline_fail", "platform") else 1
        if result.returncode != expected:
            raise AssertionError(f"retry={outcome}: expected exit {expected}, got {result.returncode}\n{result.stdout}")
        if outcome == "baseline_fail" and "Baseline fixture missing" not in result.stdout:
            raise AssertionError(f"JIT baseline failure was not reported:\n{result.stdout}")
        if outcome == "platform" and "not passes" not in result.stdout:
            raise AssertionError(f"Platform rejection was not reported separately:\n{result.stdout}")
        print(f"PASS: retry={outcome}, gate exit={result.returncode}")


if __name__ == "__main__":
    for retry_outcome in ("pass", "fail", "crash", "missing", "skip", "pass_then_crash", "baseline_fail", "platform", "platform_windows", "unrelated_platform"):
        check_retry(retry_outcome)
