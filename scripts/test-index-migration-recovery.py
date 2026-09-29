#!/usr/bin/env python3
"""Interrupt real file migrations at durable promotion, WAL, commit and checkpoint."""
import pathlib
import shutil
import subprocess
import tempfile

root = pathlib.Path(__file__).resolve().parent.parent
project = root / "tools/IndexMigrationRecovery/IndexMigrationRecovery.csproj"
subprocess.run(["dotnet", "build", str(project), "-c", "Release", "-p:TestingEnabled=true"], check=True)
runner = project.parent / "bin/Release/net8.0/IndexMigrationRecovery.dll"


# A liveness guard, not a performance budget: a fault run syncs the data or log file on every
# page write (about 3,350 fsyncs). Windows runners (FlushFileBuffers) take 20-86 s for the commit
# and checkpoint stages depending on the runner, so 90 s timed out healthy runs.
TIMEOUT = 300


def run(*args, check=True):
    try:
        return subprocess.run(["dotnet", str(runner), *map(str, args)],
                              capture_output=True, text=True, timeout=TIMEOUT, check=check)
    except subprocess.TimeoutExpired as timeout:
        # Show how far the timed-out process got (the tool reports its writes on stderr).
        for name, output in (("stdout", timeout.stdout), ("stderr", timeout.stderr)):
            if output:
                text = output.decode(errors="replace") if isinstance(output, bytes) else output
                print(f"--- {name} of the timed-out command ---\n{text[-4000:]}", flush=True)
        raise


with tempfile.TemporaryDirectory(prefix="litedb-migration-recovery-") as directory:
    directory = pathlib.Path(directory)
    for encryption in ("plain", "encrypted"):
        original = directory / (encryption + ".db")
        run("create", original, encryption)
        for mode in ("crash", "io"):
            for stage in ("promotion", "wal", "commit", "checkpoint"):
                target = directory / f"{encryption}-{mode}-{stage}.db"
                shutil.copyfile(original, target)
                result = run("fault", target, encryption, mode, stage, check=False)
                marker = f"FAULT:{mode}:{stage}"
                if result.returncode == 0 or marker not in result.stdout:
                    raise RuntimeError(f"Fault was not reached: {marker}\n{result.stdout}\n{result.stderr}")
                if mode == "io" and result.returncode != 17:
                    raise RuntimeError(f"Unexpected I/O failure exit: {result.returncode}\n{result.stderr}")
                verified = run("verify", target, encryption, check=False)
                if verified.returncode:
                    raise RuntimeError(f"Recovery failed: {encryption}/{marker}\n{verified.stdout}\n{verified.stderr}")
                print(f"PASS {encryption}: {mode} during {stage}", flush=True)
