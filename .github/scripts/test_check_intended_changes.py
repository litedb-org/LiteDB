import json
import unittest

import check_intended_changes as check
from safety_fixtures import GitRepo, run_quietly

MANIFEST = ".github/safety/intended-changes.json"
DOC = "docs/handles.md"
DOC_TEXT = ("# Handles\n\n## Disposal errors\n\nClose and checkpoint failures now propagate from `Dispose` "
            "to the caller.\nOther text.\n\n## Unrelated\n\nNothing here.\n")


def entry(**overrides):
    item = {"call": "Dispose", "dimension": "mode=shared", "change": "new-exception", "before": "none",
            "after": "System.IO.IOException", "doc": f"{DOC}#failures now propagate from",
            "reason": "Close errors were swallowed before this change."}
    item.update(overrides)
    return {key: value for key, value in item.items() if value is not None}


def manifest(*items):
    return json.dumps({"schemaVersion": 1, "changes": list(items)})


class IntendedChangesTests(unittest.TestCase):
    def run_check(self, head_manifest, base_manifest=None):
        with GitRepo() as repo:
            base = repo.commit({DOC: DOC_TEXT, MANIFEST: base_manifest or manifest()})
            repo.commit({MANIFEST: head_manifest})
            return run_quietly(check.main, ["--base", base])

    def test_empty_manifest_passes(self):
        code, output = self.run_check(manifest())
        self.assertEqual(code, 0, output)
        self.assertIn("0 intended change(s)", output)

    def test_valid_entries_by_quote_or_heading_anchor_pass(self):
        code, output = self.run_check(manifest(entry(), entry(change="latency", doc=f"{DOC}#disposal-errors")))
        self.assertEqual(code, 0, output)
        self.assertIn("2 intended change(s)", output)

    def test_invalid_entries_fail_with_their_reason(self):
        cases = {
            "change must be one of": entry(change="faster"),
            "'after' is required": entry(after=None),
            "explain the reason": entry(reason="because"),
            "does not resolve": entry(doc=f"{DOC}#this text is nowhere"),
            "does not resolve ": entry(doc="README.md#failures now propagate"),
            "does not resolve  ": entry(call="Checkpoint", doc=f"{DOC}#unrelated"),
        }
        for expected, item in cases.items():
            with self.subTest(expected):
                code, output = self.run_check(manifest(item))
                self.assertEqual(code, 1, output)
                self.assertIn(expected.strip(), output)

    def test_duplicates_fail(self):
        code, output = self.run_check(manifest(entry(), entry(reason="Another sentence of reason.")))
        self.assertEqual(code, 1, output)
        self.assertIn("listed twice", output)

    def test_entries_inherited_from_the_base_are_not_claimed(self):
        old = entry(reason="An earlier PR made this change.")
        code, output = self.run_check(manifest(old, entry()), base_manifest=manifest(old))
        self.assertEqual(code, 0, output)
        self.assertIn("1 intended change(s)", output)

    def test_covers_matches_call_change_and_dimension_glob(self):
        self.assertTrue(check.covers(entry(dimension="mode=shared;*"), "new-exception", "Dispose", "mode=shared;x=1"))
        self.assertTrue(check.covers(entry(dimension=None), "new-exception", "Dispose", "anything"))
        self.assertFalse(check.covers(entry(), "new-exception", "Dispose", "mode=direct"))
        self.assertFalse(check.covers(entry(), "exception-removed", "Dispose", "mode=shared"))

    def test_bare_list_form_is_accepted(self):
        code, output = self.run_check(json.dumps([entry()]))
        self.assertEqual(code, 0, output)


if __name__ == "__main__":
    unittest.main()
