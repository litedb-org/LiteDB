"""#2774: a Unix absolute argument must open the requested file."""
import os
import pathlib
import tempfile
import uuid
from shell_helpers import run, seed

assert os.name != "nt", "This reproduction requires Unix absolute-path syntax"
with tempfile.TemporaryDirectory(prefix="litedb-2774-") as directory:
    path = pathlib.Path(directory) / "data.db"
    marker = uuid.uuid4().hex
    seed(path, marker)
    before = path.read_bytes()
    output = run("select value from rows;\n", args=[str(path)])
    assert marker in output, "absolute argument did not open the seeded database:\n" + output
    assert path.read_bytes() == before, "reading changed the data file"
print("VERIFIED_2774: absolute argument reads the correct persisted payload")
