"""Run the real built shell with bounded input, output and lifetime."""
import pathlib
import subprocess
import tempfile
import time

ROOT = pathlib.Path(__file__).resolve().parents[2]
SHELL = ROOT / "LiteDB.Shell/bin/Release/net8.0/LiteDB.Shell.dll"


def run(commands, args=(), timeout=5):
    assert SHELL.is_file(), "Build LiteDB.sln in Release with TestingEnabled=true first"
    with tempfile.TemporaryFile() as output:
        process = subprocess.Popen(["dotnet", str(SHELL), *args], stdin=subprocess.PIPE,
                                   stdout=output, stderr=subprocess.STDOUT)
        try:
            process.stdin.write(commands.encode())
            process.stdin.close()
            deadline = time.monotonic() + timeout
            while process.poll() is None:
                if time.monotonic() >= deadline or output.tell() > 1024 * 1024:
                    raise AssertionError("shell exceeded time/output bound; possible EOF loop")
                time.sleep(0.01)
            output.seek(0)
            result = output.read(1024 * 1024).decode(errors="replace")
            assert process.returncode == 0, result
            return result
        finally:
            if process.poll() is None:
                process.kill()
            process.wait(timeout=5)


def seed(path, marker):
    run(f"open filename={path}\ninsert into rows values {{_id:1,value:'{marker}'}};\n")
    assert path.is_file(), "setup did not create the requested database"
    assert marker in run(f"open filename={path}\nselect value from rows;\n"), "setup data was not persisted"
