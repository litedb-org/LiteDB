"""#2843: EOF in SQL must terminate and preserve earlier committed commands."""
import pathlib
import tempfile
import uuid
from shell_helpers import run, seed

with tempfile.TemporaryDirectory(prefix="litedb-2843-") as directory:
    path = pathlib.Path(directory) / "data.db"
    marker = uuid.uuid4().hex
    seed(path, marker)
    # Complete SQL is a positive control: disabling execution must not pass.
    assert marker in run(f"open filename={path}\nselect value from rows;\n")
    run(f"open filename={path}\nselect value from rows")
    # Incomplete SQL must not execute just because input ended.
    unfinished = uuid.uuid4().hex
    run(f"open filename={path}\ninsert into rows values {{_id:2,value:'{unfinished}'}}")
    output = run(f"open filename={path}\nselect value from rows;\n")
    assert marker in output and unfinished not in output, output
    # --exit must also terminate when the final queued command needs another line.
    run("", args=("--exec", f"open filename={path}", "--exec", "select value from rows", "--exit"))
    assert marker in run("", args=("--exec", f"open filename={path}", "--exec", "select value from rows;", "--exit"))
    # A valid multiline command remains supported.
    assert marker in run(f"open filename={path}\nselect value\nfrom rows;\n")
    assert marker in run(f"open filename={path}\nselect value from rows;\n")
print("VERIFIED_2843: complete SQL works, incomplete EOF exits, committed data survives")
