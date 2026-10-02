import json
import tempfile
import unittest
from pathlib import Path

import mutation_gate as gate
from safety_fixtures import GitRepo, run_quietly

PATH = "LiteDB/Engine/Services/Pager.cs"
SOURCE = """using System;
using System.Collections.Generic;

namespace LiteDB.Engine
{
    internal sealed class Pager : IDisposable
    {
        private int _count;

        public Pager(int count) : base()
        {
            _count = count;
        }

        public int Next(int value)
        {
            var map = new Dictionary<int, int>(4) { [1] = 2 };
            return value + 1;
        }

        public void Flush()
        {
            try
            {
                _count++;
            }
            finally
            {
                _count--;
            }
        }

        public void ReleaseAll<T>() where T : class
        {
            _count = 0; // Close() is not called here
        }

        public void Dispose() => _count = -1;

        ~Pager()
        {
            _count = 7;
        }

        private sealed class EntryLock
        {
            public int Hold(int value) { return value * 2; }
        }
    }
}
"""
# The base differs from SOURCE on three lines: Next's return, the finally body and Dispose.
BASE = (SOURCE.replace("return value + 1;", "return value;").replace("_count--;", "_count -= 1;")
        .replace("_count = -1;", "_count = 0;"))


def line_of(snippet, text=SOURCE):
    return next(number for number, line in enumerate(text.splitlines(), 1) if snippet in line)


def at(snippet, status, mutator="Statement mutation", replacement=";", text=SOURCE, identifier=None):
    line = line_of(snippet, text)
    column = text.splitlines()[line - 1].index(snippet) + 1
    return {"id": identifier or f"{snippet}:{status}", "mutatorName": mutator, "replacement": replacement,
            "status": status, "location": {"start": {"line": line, "column": column},
                                           "end": {"line": line, "column": column + len(snippet)}}}


# A changed file outside the mutate scope: Stryker 5 still lists it, with this placeholder instead of
# its source and no mutants (excerpt of a real report: language "none", 29-character source).
OTHER = "LiteDB/Client/Coordinated/CoordinatedEngine.cs"
PLACEHOLDER = {"language": "none", "source": "File ignored by mutate filter", "mutants": []}


def stryker_report(root, mutants, source=SOURCE, key=None, extra=None):
    files = {key or f"{root}/{PATH}": {"language": "cs", "source": source, "mutants": mutants}}
    files.update({f"{root}/{name}": entry for name, entry in (extra or {}).items()})
    return {"schemaVersion": "2", "thresholds": {"high": 80, "low": 60}, "projectRoot": f"{root}/LiteDB",
            "files": files}


class MutationGateTests(unittest.TestCase):
    def run_gate(self, mutants, extra_args=(), source=SOURCE, key=None, head_files=None, extra=None):
        with GitRepo() as repo, tempfile.TemporaryDirectory() as scratch:
            base = repo.commit({PATH: BASE, OTHER: "class Other { }\n"})
            repo.commit(head_files or {PATH: SOURCE, OTHER: "class Other { int changed; }\n"})
            report = Path(scratch) / "mutation-report.json"
            report.write_text(json.dumps(stryker_report(repo.path, mutants, source, key, extra)), encoding="utf-8")
            args = [str(report), "--base", base, *[arg.replace("{scratch}", scratch) for arg in extra_args]]
            code, output = run_quietly(gate.main, args)
            written = {name.name: name.read_text(encoding="utf-8") for name in Path(scratch).iterdir()}
            return code, output, written

    def test_survivor_in_finally_or_dispose_blocks(self):
        code, output, _ = self.run_gate([at("_count--;", "Survived"), at("_count = -1;", "NoCoverage")])
        self.assertEqual(code, 1, output)
        self.assertIn(f"`{PATH}:{line_of('_count--;')}`", output)
        self.assertIn("in a finally block", output)
        self.assertIn("in method Dispose", output)
        self.assertIn("**blocking**", output)

    def test_survivor_outside_cleanup_and_lock_code_is_advisory(self):
        code, output, _ = self.run_gate([at("value + 1", "Survived", "Arithmetic mutation", "value - 1")])
        self.assertEqual(code, 0, output)
        self.assertIn("advisory", output)
        self.assertIn("| `value + 1` | `value - 1` |", output)
        self.assertIn("lines to test, not a score", output)

    def test_mutants_on_unchanged_lines_are_not_listed(self):
        code, output, _ = self.run_gate([at("_count = count;", "Survived"), at("_count = 7;", "Survived")])
        self.assertEqual(code, 0, output)
        self.assertIn("No surviving mutants on changed lines", output)
        self.assertNotIn(f":{line_of('_count = count;')}`", output)

    def test_killed_and_timeout_mutants_are_detected(self):
        code, output, _ = self.run_gate([at("_count--;", "Killed"), at("_count--;", "Timeout", identifier="t")])
        self.assertEqual(code, 0, output)
        self.assertIn("Killed 1, Timeout 1", output)
        self.assertIn("No surviving mutants on changed lines", output)

    def test_unevaluated_mutant_in_cleanup_code_warns_without_blocking(self):
        code, output, _ = self.run_gate([at("_count = -1;", "CompileError"), at("_count = -1;", "Ignored")])
        self.assertEqual(code, 0, output)
        self.assertIn("CompileError mutant in cleanup/lock code (in method Dispose) was not evaluated", output)
        self.assertNotIn("Ignored mutant in", output)
        self.assertIn("CompileError 1, Ignored 1", output)

    def test_report_from_another_revision_is_rejected(self):
        code, output, _ = self.run_gate([at("_count--;", "Killed")], source=BASE)
        self.assertEqual(code, 1, output)
        self.assertIn("differs from HEAD", output)

    def test_keys_from_another_checkout_map_by_suffix(self):
        mutant = at("_count--;", "Survived")
        code, output, _ = self.run_gate([mutant], key=f"D:\\a\\LiteDB\\LiteDB\\{PATH.replace('/', chr(92))}")
        self.assertEqual(code, 1, output)
        code, output, _ = self.run_gate([mutant], key="Engine/Services/Pager.cs")
        self.assertEqual(code, 1, output)

    def test_json_and_markdown_outputs(self):
        args = ["--json", "{scratch}/gate.json", "--markdown", "{scratch}/gate.md"]
        code, _, written = self.run_gate([at("_count--;", "Survived"), at("value + 1", "Killed")], args)
        self.assertEqual(code, 1)
        data = json.loads(written["gate.json"])
        survivors = [mutant for mutant in data["mutants"] if mutant["status"] == "Survived"]
        self.assertEqual([(m["path"], m["line"], m["blocking"]) for m in survivors],
                         [(PATH, line_of("_count--;"), True)])
        self.assertEqual(len(data["mutants"]), 2)
        self.assertIn("| Location | Status | Mutator", written["gate.md"])

    def test_advisory_mode_lists_blocking_class_survivors_but_exits_zero(self):
        code, output, _ = self.run_gate([at("_count--;", "Survived")], ["--advisory"])
        self.assertEqual(code, 0, output)
        self.assertIn("**blocking**", output)
        self.assertIn("advisory (exit 0", output)

    def test_net_modes_switch_decides_without_a_flag(self):
        modes = {".github/safety/net-modes.json": json.dumps({"blocking": False, "nets": {"mutation-gate": "x"}})}
        code, output, _ = self.run_gate([at("_count--;", "Survived")], head_files={PATH: SOURCE, **modes})
        self.assertEqual(code, 0, output)
        code, output, _ = self.run_gate([at("_count--;", "Survived")], ["--blocking"],
                                        head_files={PATH: SOURCE, **modes})
        self.assertEqual(code, 1, output)

    def test_unusable_report_fails_even_in_advisory_mode(self):
        code, output, _ = self.run_gate([at("_count--;", "Killed")], ["--advisory"], source=BASE)
        self.assertEqual(code, 1, output)

    def test_out_of_scope_placeholder_files_are_skipped_not_mismatched(self):
        for mode in ("--advisory", "--blocking"):
            code, output, _ = self.run_gate([at("value + 1", "Survived", "Arithmetic mutation", "value - 1")],
                                            [mode], extra={OTHER: PLACEHOLDER})
            self.assertEqual(code, 0, output)
            self.assertNotIn("differs from", output)
            self.assertIn("1 changed file(s) in the report have no mutants (outside the mutate scope)", output)

    def test_placeholder_source_with_mutants_is_still_a_mismatch(self):
        broken = {**PLACEHOLDER, "mutants": [at("_count--;", "Killed")]}
        code, output, _ = self.run_gate([at("value + 1", "Killed")], ["--advisory"], extra={OTHER: broken})
        self.assertEqual(code, 1, output)
        self.assertIn(f"The report's copy of {OTHER} differs from HEAD", output)

    def test_advisory_mode_exits_zero_with_findings_and_placeholders(self):
        code, output, _ = self.run_gate([at("_count--;", "Survived"), at("_count = -1;", "NoCoverage")],
                                        ["--advisory"], extra={OTHER: PLACEHOLDER})
        self.assertEqual(code, 0, output)
        self.assertIn("2 finding(s), advisory (exit 0", output)

    def test_lock_named_file_blocks_everywhere_in_it(self):
        lock_path = "LiteDB/Engine/Services/Snapshot.Lifetime.cs"
        with GitRepo() as repo, tempfile.TemporaryDirectory() as scratch:
            base = repo.commit({lock_path: BASE})
            repo.commit({lock_path: SOURCE})
            report = Path(scratch) / "r.json"
            report.write_text(json.dumps(stryker_report(repo.path, [at("value + 1", "Survived")],
                                                        key=f"{repo.path}/{lock_path}")), encoding="utf-8")
            code, output = run_quietly(gate.main, [str(report), "--base", base])
        self.assertEqual(code, 1, output)
        self.assertIn("in file Snapshot.Lifetime.cs ('lifetime')", output)


GUARDED_PATH = "LiteDB/Client/Shared/SharedMutexOwner.cs"
GUARDED = """using System;

namespace LiteDB
{
    internal sealed class SharedMutexOwner
    {
        public bool Take(int command)
        {
#if DEBUG || TESTING
            Trace(command == 1 ? "via holder" : "none");
#else
            Count(command == 1 ? 2 : 3);
#endif
            return command > 0;
        }
    }
}
"""
GUARDED_BASE = (GUARDED.replace('"via holder"', '"holder"').replace("? 2 : 3", "? 4 : 5")
                .replace("command > 0", "command >= 0"))


class TestingOnlyRegionTests(unittest.TestCase):
    def run_gate(self, mutants, extra_args=("--blocking",)):
        with GitRepo() as repo, tempfile.TemporaryDirectory() as scratch:
            base = repo.commit({GUARDED_PATH: GUARDED_BASE})
            repo.commit({GUARDED_PATH: GUARDED})
            report = Path(scratch) / "r.json"
            report.write_text(json.dumps(stryker_report(repo.path, mutants, GUARDED, f"{repo.path}/{GUARDED_PATH}")),
                              encoding="utf-8")
            return run_quietly(gate.main, [str(report), "--base", base, "--json", f"{scratch}/g.json", *extra_args]) \
                + (json.loads((Path(scratch) / "g.json").read_text(encoding="utf-8")),)

    def test_survivor_compiled_only_under_debug_or_testing_is_advisory(self):
        mutant = at('"via holder"', "Survived", "String mutation", '""', text=GUARDED)
        code, output, data = self.run_gate([mutant])
        self.assertEqual(code, 0, output)
        self.assertIn("advisory (DEBUG/TESTING only)", output)
        self.assertIn("compiled only under DEBUG or TESTING", output)
        self.assertIn("Surviving mutants: 0 blocking, 1 in cleanup/lock code compiled only under DEBUG or TESTING "
                      "(advisory), 0 advisory elsewhere", output)
        record = data["mutants"][0]
        self.assertEqual((record["inBlockingScope"], record["testingOnly"], record["blocking"]), (True, True, False))

    def test_else_branch_of_a_testing_guard_and_code_after_it_still_block(self):
        for snippet, replacement in (("? 2 : 3", "? 3 : 2"), ("command > 0", "command < 0")):
            mutant = at(snippet, "Survived", "Equality mutation", replacement, text=GUARDED)
            code, output, data = self.run_gate([mutant])
            self.assertEqual(code, 1, output)
            self.assertIn("**blocking**", output)
            self.assertFalse(data["mutants"][0]["testingOnly"], snippet)

    def test_unevaluated_testing_only_mutant_does_not_warn(self):
        mutant = at('"via holder"', "CompileError", "String mutation", '""', text=GUARDED)
        code, output, _ = self.run_gate([mutant])
        self.assertEqual(code, 0, output)
        self.assertNotIn("was not evaluated", output)


class ReleaseExcludedLinesTests(unittest.TestCase):
    def excluded(self, text):
        lines = text.splitlines()
        return [lines[number - 1].strip() for number in sorted(gate.release_excluded_lines(text))]

    def test_debug_and_testing_branches_are_excluded_their_else_is_not(self):
        text = "\n".join(["#if DEBUG || TESTING", "a();", "#else", "b();", "#endif",
                          "#if TESTING", "c();", "#endif", "#if DEBUG", "d();", "#endif",
                          "#if (TESTING) // instrumentation", "e();", "#endif", "  #  if !TESTING", "f();", "#endif"])
        self.assertEqual(self.excluded(text), ["a();", "c();", "d();", "e();"])

    def test_other_symbols_are_unknown_so_only_definitely_false_branches_go(self):
        text = "\n".join(["#if DEBUG || NET8_0_OR_GREATER", "a();", "#endif",
                          "#if DEBUG && NET8_0_OR_GREATER", "b();", "#endif",
                          "#if NET8_0_OR_GREATER", "c();", "#elif TESTING", "d();", "#else", "e();", "#endif",
                          "#if TESTING", "f();", "#elif NET8_0_OR_GREATER", "g();", "#elif DEBUG", "h();",
                          "#else", "i();", "#endif",
                          "#if TESTING == false", "j();", "#endif", "#if TESTING != true", "k();", "#endif",
                          "#if false", "l();", "#endif", "#if !(DEBUG || TESTING) && X", "m();", "#endif"])
        self.assertEqual(self.excluded(text), ["b();", "d();", "f();", "h();", "l();"])

    def test_nesting_excludes_everything_under_a_false_branch(self):
        text = "\n".join(["#if NET8_0_OR_GREATER", "a();", "#if TESTING", "b();", "#else", "c();", "#endif",
                          "d();", "#endif", "#if DEBUG || TESTING", "e();", "#if NET8_0_OR_GREATER", "f();",
                          "#else", "g();", "#endif", "h();", "#endif", "i();"])
        self.assertEqual(self.excluded(text), ["b();", "e();", "f();", "g();", "h();"])

    def test_directives_in_comments_and_strings_and_unparsable_conditions(self):
        text = "\n".join(['var s = @"', "#if TESTING", "x", '";', "/*", "#if DEBUG", "*/", "a();",
                          "#if TESTING ||", "b();", "#endif", "#region TESTING", "c();", "#endregion"])
        self.assertEqual(self.excluded(text), [])


class StructureTests(unittest.TestCase):
    def classify(self, snippet):
        line = line_of(snippet)
        column = SOURCE.splitlines()[line - 1].index(snippet) + 1
        return gate.Structure(SOURCE).classify(PATH, line, column)

    def test_cleanup_methods_finalizers_and_finally_blocks(self):
        self.assertEqual(self.classify("_count = 0;"), (True, "in method ReleaseAll"))
        self.assertEqual(self.classify("_count = -1;"), (True, "in method Dispose"))
        self.assertEqual(self.classify("_count = 7;"), (True, "in finalizer ~Pager"))
        self.assertTrue(self.classify("_count--;")[1].startswith("in a finally block"))

    def test_ordinary_code_constructors_and_initializers_are_not_cleanup(self):
        self.assertEqual(self.classify("_count++;"), (False, "outside cleanup and lock code"))
        self.assertEqual(self.classify("_count = count;"), (False, "outside cleanup and lock code"))
        self.assertEqual(self.classify("[1] = 2"), (False, "outside cleanup and lock code"))

    def test_lock_named_nested_type(self):
        self.assertEqual(self.classify("value * 2"), (True, "in type EntryLock ('lock')"))

    def test_lock_words_match_whole_words_only(self):
        for name in ("CollectionLock", "TransactionGate", "TransactionMonitor", "SharedMutexOwner",
                     "SharedMutexPin", "SharedTurnstile", "OperationLifetime", "LockService", "Snapshot.Lifetime",
                     "PinnedPages", "LOCK_TABLE"):
            self.assertIsNotNone(gate.lock_word(name), name)
        for name in ("Block", "Aggregate", "EntityMapping", "Clock", "Delegate", "Spinner", "Navigate"):
            self.assertIsNone(gate.lock_word(name), name)


if __name__ == "__main__":
    unittest.main()
