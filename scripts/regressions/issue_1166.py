"""#1166: quoted bare filenames with spaces must resolve to the intended file."""
import pathlib
import tempfile
import uuid
from shell_helpers import run, seed

with tempfile.TemporaryDirectory(prefix="litedb 1166 ") as directory:
    path = pathlib.Path(directory) / "data with spaces.db"
    marker = uuid.uuid4().hex
    seed(path, marker)
    before = path.read_bytes()
    output = run(f'open "{path}"\nselect value from rows;\n')
    assert marker in output, "quoted bare filename did not read the intended database:\n" + output
    assert path.read_bytes() == before, "reading changed the original file"
    assert set(pathlib.Path(directory).iterdir()) == {path}, "shell created an unintended sibling file"
print("VERIFIED_1166: quoted path resolves correctly and preserves data")
