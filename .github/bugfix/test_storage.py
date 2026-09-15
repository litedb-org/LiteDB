"""External Git/gh JSON and text must retain UTF-8 identities on Windows."""

import json
import sys
import unittest
from unittest.mock import patch

from storage import github, run


class EncodingTests(unittest.TestCase):
    def test_repository_metadata_uses_canonical_endpoint(self):
        # GitHub returns 404 for /repos/owner/repo/ but accepts /repos/owner/repo.
        # Hosted initialization reads default_branch before acquiring its lease.
        with patch("storage.run", return_value='{"default_branch":"dev"}') as command:
            self.assertEqual("dev", github("owner/repo", "")["default_branch"])
            command.assert_called_once_with(["gh", "api", "repos/owner/repo"], infrastructure=True)
        with patch("storage.run", return_value='{"id":123}') as command:
            self.assertEqual(123, github("owner/repo", "actions/runs/123")["id"])
            command.assert_called_once_with(["gh", "api", "repos/owner/repo/actions/runs/123"], infrastructure=True)

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
