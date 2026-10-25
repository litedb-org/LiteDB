import hashlib
import importlib.util
import pathlib
import subprocess
import tempfile
import unittest


ROOT = pathlib.Path(__file__).resolve().parents[2]
MODULE_PATH = ROOT / "scripts/release_compatibility.py"
SPEC = importlib.util.spec_from_file_location("release_compatibility", MODULE_PATH)
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


class ReleaseCompatibilitySourceTests(unittest.TestCase):
    def test_provenance_inputs_are_checked_out_as_lf(self):
        paths = (
            "scripts/generate-release-corpus.py",
            "scripts/test-release-compatibility.py",
            "scripts/release_compatibility.py",
            "tools/ReleaseCompatibility/provenance/generate-release-corpus.py",
            "tools/ReleaseCompatibility/Corpus.cs",
        )
        result = subprocess.run(
            ["git", "check-attr", "eol", "--", *paths],
            cwd=ROOT,
            check=True,
            capture_output=True,
            text=True,
        )
        self.assertEqual(
            [f"{path}: eol: lf" for path in paths], result.stdout.splitlines()
        )

    def test_crlf_source_is_refused_before_hashing(self):
        with tempfile.NamedTemporaryFile() as source:
            source.write(b"producer\r\n")
            source.flush()
            with self.assertRaisesRegex(RuntimeError, "requires an LF checkout"):
                MODULE.source_sha256(source.name)

    def test_lf_source_hashes_raw_bytes(self):
        data = b"producer\n"
        with tempfile.NamedTemporaryFile() as source:
            source.write(data)
            source.flush()
            self.assertEqual(hashlib.sha256(data).hexdigest(), MODULE.source_sha256(source.name))


if __name__ == "__main__":
    unittest.main()
