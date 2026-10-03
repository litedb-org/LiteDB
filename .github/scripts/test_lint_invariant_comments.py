import os
import tempfile
import unittest

import check_pr_section
import lint_invariant_comments as lint
from safety_fixtures import GitRepo, run_quietly, csharp_class

PATH = "LiteDB/Client/Shared/SharedMutexOwner.cs"
OTHER = "LiteDB/Client/Shared/SharedMutexPin.cs"
TESTS = "LiteDB.Tests/Shared/Owner_Tests.cs"
INVARIANT = ("        // Waiters block on the mutex itself and must never poll: a timed wait\n"
             "        // would let a later waiter win the handoff.\n")
SOURCE = ("namespace LiteDB\n{\n    internal class SharedMutexOwner\n    {\n        void Acquire()\n        {\n"
          f"{INVARIANT}            _mutex.WaitOne();\n            // plain note\n        }}\n    }}\n}}\n")


def body(entries):
    return "## Summary\n\nChange.\n\n### Moved invariants\n\n" + entries + "\n\n## Other\n\n- unrelated -> x\n"


class InvariantCommentLintTests(unittest.TestCase):
    def run_lint(self, after, pr_body=None, extra=None):
        with GitRepo() as repo:
            base = repo.commit({PATH: SOURCE, TESTS: csharp_class("Owner_Tests", {"Waits_In_Order": ("Fact", "")})})
            repo.commit({PATH: after, **(extra or {})})
            argv = ["--base", base]
            if pr_body is not None:
                handle, path = tempfile.mkstemp(suffix=".md")
                with os.fdopen(handle, "w", encoding="utf-8") as stream:
                    stream.write(pr_body)
                argv += ["--pr-body-file", path]
            try:
                return run_quietly(lint.main, argv)
            finally:
                if pr_body is not None:
                    os.remove(path)

    def deleted(self):
        return SOURCE.replace(INVARIANT, "")

    def test_deleted_invariant_is_listed_as_a_warning_without_a_body(self):
        code, output = self.run_lint(self.deleted())
        self.assertEqual(code, 0, output)
        self.assertIn("1 deleted invariant comment(s)", output)
        self.assertIn("must never poll", output)
        self.assertIn(f"{PATH}:7", output)

    def test_deletion_without_a_moved_invariants_entry_fails(self):
        code, output = self.run_lint(self.deleted(), pr_body="## Summary\n\nNothing.\n")
        self.assertEqual(code, 1, output)
        self.assertIn("needs a '### Moved invariants' entry", output)

    def test_entry_by_location_or_quote_with_a_resolving_reference_passes(self):
        for entry in (f"- {PATH}:7 → {TESTS}#Waits_In_Order",
                      "- SharedMutexOwner.cs:8 -> [test: Owner_Tests#Waits_In_Order]",
                      f"- \"waiters block on the mutex\" → {OTHER}:3"):
            with self.subTest(entry):
                code, output = self.run_lint(self.deleted(), pr_body=body(entry), extra={OTHER: "a\nb\nc\n"})
                self.assertEqual(code, 0, output)

    def test_entry_must_name_a_reference_that_resolves(self):
        for entry in (f"- {PATH}:7 → enforced by careful review",
                      f"- {PATH}:7 → {TESTS}#Missing_Test",
                      f"- {PATH}:40 → {TESTS}#Waits_In_Order"):
            with self.subTest(entry):
                code, output = self.run_lint(self.deleted(), pr_body=body(entry))
                self.assertEqual(code, 1, output)

    def test_moved_reflowed_or_unrelated_comments_are_not_deletions(self):
        moved = self.deleted().replace("// plain note", "// Waiters block on the mutex itself and must never\n"
                                                        "            // poll: a timed wait would let a later waiter"
                                                        " win the handoff.")
        for after in (moved, SOURCE.replace("// plain note", "// another note"),
                      SOURCE.replace("            // plain note\n", "")):
            with self.subTest(after):
                code, output = self.run_lint(after, pr_body="no section")
                self.assertEqual(code, 0, output)

    def test_trigger_words_are_kept_in_one_list(self):
        for text in ("must not be held", "never blocks", "class invariant", "do not dispose", "don't",
                     "must run before the flush", "must happen after close", "block without polling",
                     "a releaser cannot barge ahead"):
            self.assertTrue(lint.TRIGGERS.search(text), text)
        for text in ("must be held", "before the flush", "notes"):
            self.assertFalse(lint.TRIGGERS.search(text), text)

    def test_pr_section_check_runs_the_moved_invariants_check(self):
        section = ("## Safety / regression evidence\n\n- Not applicable: the change only touches comments "
                   "in one file.\n")
        with GitRepo() as repo:
            base = repo.commit({PATH: SOURCE})
            repo.commit({PATH: self.deleted()})
            handle, path = tempfile.mkstemp(suffix=".md")
            with os.fdopen(handle, "w", encoding="utf-8") as stream:
                stream.write(section)
            try:
                code, output = run_quietly(check_pr_section.main, ["--body", path, "--base", base])
            finally:
                os.remove(path)
        self.assertEqual(code, 1, output)
        self.assertIn("Moved invariants", output)


if __name__ == "__main__":
    unittest.main()
