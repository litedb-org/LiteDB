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
    assert marker in run(f"open filename={path}\nselect value from rows;\n")
print("VERIFIED_2843: complete SQL works, incomplete EOF exits, committed data survives")
