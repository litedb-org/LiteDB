import json
import unittest

import lint_doc_claims as lint
import safety_common as common
from safety_fixtures import GitRepo, run_quietly, csharp_class

DOC = "docs/shared-mode.md"
TESTS = "LiteDB.Tests/Shared/Close_Tests.cs"
MARKERS = ".github/safety/markers.json"
BASE_DOC = ("# Shared mode\n\nClose errors always propagate to the caller.\n\n"
            "```csharp\n// never judged inside code\n```\n")


class DocClaimLintTests(unittest.TestCase):
    def run_lint(self, doc, extra=None, argv=None):
        with GitRepo() as repo:
            base = repo.commit({DOC: BASE_DOC, TESTS: csharp_class("Close_Tests", {"Close_Propagates": ("Fact", "")},
                                                                   namespace="LiteDB.Tests.Shared")})
            repo.commit({DOC: doc, **(extra or {})})
            return run_quietly(lint.main, argv or ["--base", base])

    def test_new_claim_without_anchor_fails(self):
        code, output = self.run_lint(BASE_DOC + "\nA failed checkpoint never loses acknowledged writes.\n")
        self.assertEqual(code, 1, output)
        self.assertIn("never loses acknowledged writes", output)
        self.assertIn(f"{DOC}:9", output)

    def test_existing_unchanged_or_reflowed_claims_are_not_flagged(self):
        reflowed = BASE_DOC.replace("always propagate to the caller.", "always propagate\nto the caller.")
        for doc in (BASE_DOC + "\nA plain new sentence.\n", reflowed):
            with self.subTest(doc):
                code, output = self.run_lint(doc)
                self.assertEqual(code, 0, output)

    def test_changed_sentence_is_judged_again(self):
        code, output = self.run_lint(BASE_DOC.replace("always propagate to the caller", "always propagate to callers"))
        self.assertEqual(code, 1, output)

    def test_resolving_anchors_pass(self):
        for anchor in (f"[test: {TESTS}#Close_Propagates]", "[test: Close_Tests#Close_Propagates]",
                       "[test: LiteDB.Tests.Shared.Close_Tests.Close_Propagates]", "[marker: refusal:closed]"):
            with self.subTest(anchor):
                doc = BASE_DOC + f"\nA closed database rejects new readers {anchor}.\n"
                code, output = self.run_lint(doc, extra={MARKERS: json.dumps({"markers": [{"name": "refusal:closed"}]})})
                self.assertEqual(code, 0, output)

    def test_anchor_after_the_full_stop_stays_on_its_sentence(self):
        doc = BASE_DOC + "\nA closed database rejects new readers. [test: Close_Tests#Close_Propagates] Next one.\n"
        code, output = self.run_lint(doc)
        self.assertEqual(code, 0, output)

    def test_unresolved_anchors_fail(self):
        for anchor in ("[test: Close_Tests#Missing]", f"[test: {TESTS}#Missing]", "[marker: refusal:unknown]"):
            with self.subTest(anchor):
                doc = BASE_DOC + f"\nA closed database rejects new readers {anchor}.\n"
                code, output = self.run_lint(doc, extra={MARKERS: json.dumps({"markers": ["refusal:closed"]})})
                self.assertEqual(code, 1, output)
                self.assertIn("does not resolve", output)

    def test_marker_without_registry_is_a_warning(self):
        code, output = self.run_lint(BASE_DOC + "\nA closed database rejects new readers [marker: refusal:x].\n")
        self.assertEqual(code, 0, output)
        self.assertIn("markers.json does not exist", output)

    def test_trigger_words_in_code_spans_tables_and_lists(self):
        doc = BASE_DOC + "\nThe `never` option is a name.\n\n| Case | Result |\n| --- | --- |\n| close | refuses |\n"
        code, output = self.run_lint(doc)
        self.assertEqual(code, 1, output)
        self.assertIn("| close | refuses |", output)
        self.assertNotIn("The `never` option", output)

    def test_all_mode_reports_backlog_without_failing(self):
        code, output = self.run_lint(BASE_DOC, argv=["--all"])
        self.assertEqual(code, 0, output)
        self.assertIn("1 normative sentence(s) judged, 1 without a resolving anchor", output)

    def test_sentence_split_keeps_line_numbers(self):
        units = list(common.doc_sentences("Intro line\ncontinues here. Second\nsentence.\n\n- item one\n"))
        self.assertEqual(units, [(1, 2, "Intro line continues here."), (2, 3, "Second sentence."),
                                 (5, 5, "- item one")])


if __name__ == "__main__":
    unittest.main()
