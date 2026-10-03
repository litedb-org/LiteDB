import json
import os
import tempfile
import unittest

import classify_critical as classify
from safety_fixtures import GitRepo, run_quietly

QUERY = "LiteDB/Engine/Query/QueryExecutor.cs"
API = "LiteDB/Client/Database/ILiteDatabase.cs"
OBLIGATIONS = ".github/safety/fuzz-obligations.json"
CONTRACTS = ".github/safety/contracts.json"
BASE = {
    QUERY: "namespace LiteDB\n{\n    internal class QueryExecutor\n    {\n        void Run()\n        {\n"
           "            var x = 1;\n        }\n    }\n}\n",
    API: "namespace LiteDB\n{\n    public interface ILiteDatabase\n    {\n        bool BeginTrans();\n    }\n}\n",
    "docs/guide.md": "# Guide\n",
}


def edit_query(line):
    return BASE[QUERY].replace("            var x = 1;\n", f"            var x = 1;\n            {line}\n")


class ClassifyCriticalTests(unittest.TestCase):
    def classify(self, change, base_extra=None):
        with GitRepo() as repo:
            base = repo.commit({**BASE, **(base_extra or {})})
            head = repo.commit(change)
            handle, path = tempfile.mkstemp(suffix=".json")
            os.close(handle)
            try:
                code, output = run_quietly(classify.main, ["--base", base, "--head", head, "--output", path])
                with open(path, encoding="utf-8") as stream:
                    result = json.load(stream)
            finally:
                os.remove(path)
        self.assertEqual(code, 0, output)
        return result, {item["rule"] for item in result["reasons"]}

    def test_documentation_and_ordinary_query_code_are_not_critical(self):
        result, rules = self.classify({"docs/guide.md": "# Guide\n\nMore.\n", QUERY: edit_query("x++;")})
        self.assertFalse(result["critical"], result)
        self.assertEqual(rules, set())

    def test_plan_directories_are_critical_by_path(self):
        for path, rule in (("LiteDB/Engine/Services/LockService.cs", "engine-services"),
                           ("LiteDB/Client/Shared/SharedEngine.cs", "shared-client"),
                           ("LiteDB/Client/Transactions/LiteTransaction.cs", "transactions-client"),
                           ("LiteDB/Client/Direct/DirectEnginePool.cs", "direct-client")):
            with self.subTest(path=path):
                result, rules = self.classify({path: "class X { }\n"})
                self.assertTrue(result["critical"])
                self.assertIn(rule, rules)

    def test_code_line_heuristics(self):
        cases = {"stream.Dispose();": "teardown-path", "finally { }": "teardown-path",
                 "_disk.Flush();": "wal-checkpoint-flush", "_walIndex.Clear();": "wal-checkpoint-flush",
                 "this.Checkpoint();": "wal-checkpoint-flush",
                 "throw new InvalidOperationException();": "exception-contract"}
        for line, rule in cases.items():
            with self.subTest(line=line):
                result, rules = self.classify({QUERY: edit_query(line)})
                self.assertIn(rule, rules, result)

    def test_heuristics_ignore_comments_and_similar_words(self):
        for line in ("// Dispose and Flush() and throw are only mentioned here", "var wall = walk;"):
            with self.subTest(line=line):
                result, _ = self.classify({QUERY: edit_query(line)})
                self.assertFalse(result["critical"], result)

    def test_a_removed_catch_widens_the_exception_contract(self):
        base = {QUERY: edit_query("try { } catch (IOException) { }")}
        result, rules = self.classify({QUERY: edit_query("try { } finally { }")}, base_extra=base)
        self.assertIn("exception-contract", rules)
        self.assertTrue(any(item["detail"].startswith("- ") for item in result["reasons"]
                            if item["rule"] == "exception-contract"))

    def test_a_new_interface_member_is_public_api_without_the_public_keyword(self):
        changed = BASE[API].replace("bool BeginTrans();", "bool BeginTrans();\n        void Ping();")
        result, rules = self.classify({API: changed})
        self.assertIn("public-api", rules)
        self.assertIn("api:ILiteDatabase.Ping", [item["detail"] for item in result["reasons"]])

    def test_safety_machinery_is_critical_including_the_contract_paths(self):
        contracts = json.dumps({"contracts": [{"id": "safety-machinery", "paths": ["tools/oracle/**"]}]})
        for path in (".github/workflows/ci.yml", "LiteDB.Fuzz/Corpus/regressions.json", "tools/oracle/x.py",
                     ".github/pull_request_template.md"):
            with self.subTest(path=path):
                result, rules = self.classify({path: "changed\n"}, base_extra={CONTRACTS: contracts})
                self.assertIn("safety-machinery", rules, result)

    def test_obligation_map_kinds_are_reused_and_other_kinds_are_not(self):
        policy = {"obligations": [
            {"id": "waits", "kind": "lock-wait", "reason": "waits", "paths": ["LiteDB/**/*.cs"],
             "patterns": [r"\.Wait\("]},
            {"id": "query-engine", "kind": "query", "reason": "query", "paths": ["LiteDB/Engine/Query/**"]},
            {"id": "broken", "kind": "shared", "reason": "x", "paths": ["LiteDB/**"], "patterns": ["("]}]}
        result, rules = self.classify({QUERY: edit_query("_event.Wait();")},
                                      base_extra={OBLIGATIONS: json.dumps(policy)})
        self.assertIn("obligation:waits", rules)
        self.assertNotIn("obligation:query-engine", rules)
        self.assertNotIn("obligation:broken", rules)

    def test_comment_names_the_reasons_and_never_gates(self):
        result = {"critical": True, "reasons": [{"rule": "shared-client", "reason": "r",
                                                 "file": "LiteDB/Client/Shared/`x`@y.cs", "detail": "path"}]}
        text = classify.comment(result, "someone")
        self.assertIn("does not block merging", text)
        self.assertIn("shared-client", text)
        self.assertNotIn("`x`", text)
        self.assertNotIn("@y", text)


if __name__ == "__main__":
    unittest.main()
