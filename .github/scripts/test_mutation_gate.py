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


def stryker_report(root, mutants, source=SOURCE, key=None):
    return {"schemaVersion": "1", "thresholds": {"high": 80, "low": 60}, "projectRoot": f"{root}/LiteDB",
            "files": {key or f"{root}/{PATH}": {"language": "cs", "source": source, "mutants": mutants}}}


class MutationGateTests(unittest.TestCase):
    def run_gate(self, mutants, extra_args=(), source=SOURCE, key=None, head_files=None):
        with GitRepo() as repo, tempfile.TemporaryDirectory() as scratch:
            base = repo.commit({PATH: BASE})
            repo.commit(head_files or {PATH: SOURCE})
            report = Path(scratch) / "mutation-report.json"
            report.write_text(json.dumps(stryker_report(repo.path, mutants, source, key)), encoding="utf-8")
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
