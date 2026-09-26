"""Exercise storage setup in fresh test hosts, including discovery failures.

CI uses ordinary temporary directories to validate routing on every OS; local
Linux runs keep this harness's own output on /dev/shm as well.
"""
import argparse
import os
from pathlib import Path
import subprocess
import tempfile
import xml.etree.ElementTree as ET


repo = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser()
parser.add_argument("--framework", choices=("net8.0", "net10.0"), default="net10.0")
parser.add_argument("--suite", choices=("LiteDB.Tests", "LiteDB.Fuzz.Tests", "LiteDB.ReproRunner.Tests"), default="LiteDB.Tests")
args = parser.parse_args()
assembly = repo / args.suite / "bin/Release" / args.framework / (args.suite + ".dll")
minimum_tests = {"LiteDB.Tests": 7, "LiteDB.Fuzz.Tests": 9, "LiteDB.ReproRunner.Tests": 45}[args.suite]
ns = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
ci = any(os.environ.get(key, "").lower() in ("1", "true") for key in ("CI", "GITHUB_ACTIONS"))
temporary_root = None
if not ci and os.environ.get("LITEDB_TEST_STORAGE") != "disk":
    temporary_root = os.environ.get("LITEDB_TEST_TEMP_ROOT")
    if not temporary_root and Path("/dev/shm").is_dir():
        temporary_root = "/dev/shm"
    if not temporary_root or not Path(temporary_root).is_absolute() or not Path(temporary_root).is_dir():
        raise SystemExit("Set LITEDB_TEST_TEMP_ROOT to an existing absolute RAM-disk path.")

with tempfile.TemporaryDirectory(prefix="litedb-storage-harness-", dir=temporary_root) as root:
    root = Path(root)
    ram = root / "configured-root"
    ram.mkdir()
    sentinel = ram / "unrelated.txt"
    sentinel.write_text("preserve unrelated data")
    cases = [
        ("configured", {"CI": "false", "LITEDB_TEST_TEMP_ROOT": str(ram)}, None),
        ("ci", {"CI": "true", "LITEDB_TEST_TEMP_ROOT": str(root / "ignored")}, None),
        ("override-ci", {"CI": "true", "LITEDB_TEST_STORAGE": "ram", "LITEDB_TEST_TEMP_ROOT": str(ram)}, None),
        ("disk", {"LITEDB_TEST_STORAGE": "disk", "LITEDB_TEST_TEMP_ROOT": str(root / "ignored")}, None),
        ("invalid", {"LITEDB_TEST_STORAGE": "typo"}, "LITEDB_TEST_STORAGE must be"),
        ("missing", {"LITEDB_TEST_STORAGE": "ram", "LITEDB_TEST_TEMP_ROOT": str(root / "missing")}, "RAM-disk root does not exist"),
        ("relative", {"LITEDB_TEST_STORAGE": "ram", "LITEDB_TEST_TEMP_ROOT": "relative"}, "must be an absolute RAM-disk path"),
    ]
    if Path("/dev/shm").is_dir():
        cases.append(("automatic-linux", {}, None))
    for name, overrides, error in cases:
        env = dict(os.environ)
        for key in ("CI", "GITHUB_ACTIONS", "LITEDB_TEST_STORAGE", "LITEDB_TEST_TEMP_ROOT"):
            env.pop(key, None)
        env.update({"TMPDIR": str(root), "TEMP": str(root), "TMP": str(root)})
        env.update(overrides)
        result = root / (name + ".trx")
        command = ["dotnet", "vstest", str(assembly),
                   "/Settings:" + str(repo / "tests.runsettings"),
                   "/Logger:trx;LogFileName=" + str(result)]
        if args.suite == "LiteDB.Tests":
            command.append("/TestCaseFilter:FullyQualifiedName~TestStorage_Tests")
        run = subprocess.run(command, cwd=repo, env=env, text=True,
                             stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=90)
        if error:
            assert run.returncode != 0 and error in run.stdout, run.stdout
        else:
            assert run.returncode == 0, run.stdout
            counters = ET.parse(result).find(".//t:Counters", ns).attrib
            assert int(counters["passed"]) >= minimum_tests and int(counters["failed"]) == 0, counters
        assert sentinel.read_text() == "preserve unrelated data"
        assert not list(ram.glob("litedb-tests-*")), "test host did not clean up its directory"
        print(name + ": passed", flush=True)
