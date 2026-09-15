"""External Git/gh JSON and text must retain UTF-8 identities on Windows."""

import json
import sys
import unittest
from unittest.mock import patch

from storage import run


class EncodingTests(unittest.TestCase):
    def test_utf8_output_ignores_legacy_host_codepage(self):
        expected = {"name": "case(bytes: [0, 0, ···])", "summary": "Größe"}
        payload = json.dumps(expected, ensure_ascii=False).encode("utf-8")
        command = [sys.executable, "-c", f"import sys; sys.stdout.buffer.write({payload!r})"]
        with patch("locale.getencoding", return_value="cp1252"):
            self.assertEqual(expected, json.loads(run(command)))

    def test_utf8_input_is_not_encoded_with_host_codepage(self):
        text = "case(bytes: [0, 0, ···])"
        command = [sys.executable, "-c", "import sys; sys.stdout.buffer.write(sys.stdin.buffer.read())"]
        with patch("locale.getencoding", return_value="cp1252"):
            self.assertEqual(text, run(command, input_text=text))


if __name__ == "__main__":
    unittest.main()
