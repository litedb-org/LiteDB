import json
import os
import tempfile
import unittest
from pathlib import Path

import select_fuzz_targets as select
from safety_fixtures import GitRepo, run_quietly

OBLIGATIONS = ".github/safety/fuzz-obligations.json"
COVERAGE = ".github/safety/fuzz-coverage-map.json"
SHARED = "LiteDB/Client/Shared/SharedEngine.cs"
QUERY = "LiteDB/Engine/Query/QueryExecutor.cs"
OTHER = "LiteDB/Utils/Unmapped.cs"
HELPER = "LiteDB.Fuzz/Targets/QueryModel.cs"


def target(cls, name, body=""):
    return f'namespace LiteDB.Fuzz.Targets;\ninternal sealed class {cls} : IFuzzTarget\n{{\n    public string Name => "{name}";\n{body}}}\n'


FILES = {
    SHARED: "class SharedEngine { void Run() { } }\n",
    QUERY: "class QueryExecutor\n{\n    void Run()\n    {\n    }\n}\n",
    OTHER: "class Unmapped { }\n",
    "docs/guide.md": "# Guide\n",
    "LiteDB.Fuzz/Targets/SharedProcessFuzzer.cs": target("SharedProcessFuzzer", "shared"),
    "LiteDB.Fuzz/Targets/QueryFuzzer.cs": target("QueryFuzzer", "query", "    QueryModel model;\n"),
    "LiteDB.Fuzz/Targets/ChaosFuzzer.cs": target("ChaosFuzzer", "chaos"),
    "LiteDB.Fuzz/Targets/IndexFuzzer.cs": target("IndexFuzzer", "index"),
    HELPER: "namespace LiteDB.Fuzz.Targets;\ninternal sealed class QueryModel { }\n",
    "LiteDB.Fuzz/SelfTests/DeadlineSelfTestFuzzer.cs": target("DeadlineSelfTestFuzzer", "deadline-selftest"),
}


def obligation(id, targets, paths, **extra):
    return {"id": id, "kind": extra.pop("kind", "shared"), "reason": "Plan row: the reason for this obligation.",
            "paths": paths, "targets": targets, **extra}


def policy(**overrides):
    document = {
        "schemaVersion": 1,
        "description": "Obligations first, coverage second, all targets otherwise.",
        "alwaysForLiteDB": ["lifetime-chaos"],
        "pendingTargets": ["lifetime-chaos", "teardown-faults"],
        "pendingPaths": ["LiteDB.Tests/Engine/ConcurrencyExplorer/**"],
        "ignore": ["docs/**", "**/*.md"],
        "obligations": [
            obligation("shared-mode", ["shared", "lifetime-chaos"], ["LiteDB/Client/Shared/**"]),
            obligation("teardown", ["teardown-faults", "chaos"], ["LiteDB/**/*.cs"], kind="teardown",
                       patterns=[r"\bfinally\b", r"\bDispose\b"], decides=False),
            obligation("fuzz-target-sources", ["@fuzz-source-users"], ["LiteDB.Fuzz/Targets/**"], kind="fuzz-harness"),
            obligation("explorer", ["chaos"], ["LiteDB.Tests/Engine/ConcurrencyExplorer/**"], kind="safety-machinery"),
        ],
    }
    document.update(overrides)
    return json.dumps(document, indent=2)


COVERAGE_MAP = json.dumps({"schemaVersion": 1, "generatedFrom": {}, "targets": {
    "query": [QUERY, SHARED], "index": [QUERY], "shared": [SHARED], "chaos": [SHARED]}})

QUERY_FINALLY = "class QueryExecutor\n{\n    void Run()\n    {\n        try { } finally { }\n    }\n}\n"


class SelectionTests(unittest.TestCase):
    def setUp(self):
        self.scratch = Path(tempfile.mkdtemp(prefix="fuzz-select-"))

    def run_select(self, changes, extra=(), files=None, diff=None, obligations=None):
        """Commit a base and a head with `changes`; return (code, output, selection JSON)."""
        with GitRepo() as repo:
            repo.commit({**FILES, OBLIGATIONS: obligations or policy(), COVERAGE: COVERAGE_MAP, **(files or {})})
            repo.commit(changes)
            output = self.scratch / "selection.json"
            argv = ["--base", "HEAD~1", "--output", str(output), *extra]
            if diff is not None:
                argv += ["--diff", diff]
            code, text = run_quietly(select.main, argv)
            return code, text, json.loads(output.read_text()) if output.exists() else None

    def decision(self, result, path):
        return next(item for item in result["decisions"] if item["file"] == path)

    def test_obligation_decides_and_reports_missing_targets_without_failing(self):
        code, output, result = self.run_select({SHARED: "class SharedEngine { void Run() { Wait(); } }\n"})
        self.assertEqual(code, 0, output)
        self.assertEqual(self.decision(result, SHARED)["decidedBy"], "obligation")
        self.assertEqual(self.decision(result, SHARED)["obligations"], ["shared-mode", "alwaysForLiteDB"])
        self.assertEqual(result["targets"], ["shared"])
        self.assertEqual(result["missing"], ["lifetime-chaos"])
        self.assertFalse(result["allTargets"])
        self.assertIn("lifetime-chaos is not available", output)
        self.assertEqual(result["seed"], 2947)

    def test_strict_fails_on_a_missing_required_target(self):
        code, _, result = self.run_select({SHARED: "class SharedEngine { }\n"}, extra=["--strict"])
        self.assertEqual(code, 1)
        self.assertEqual(result["missing"], ["lifetime-chaos"])

    def test_coverage_decides_a_file_no_obligation_matched(self):
        code, output, result = self.run_select({QUERY: QUERY_FINALLY.replace("try { } finally { }", "Plan();")})
        self.assertEqual(code, 0, output)
        decision = self.decision(result, QUERY)
        self.assertEqual(decision["decidedBy"], "coverage")
        self.assertEqual(decision["obligations"], ["alwaysForLiteDB"])
        self.assertEqual(result["targets"], ["index", "query"])

    def test_a_cross_cutting_pattern_adds_targets_but_keeps_the_coverage_stage(self):
        _, _, result = self.run_select({QUERY: QUERY_FINALLY})
        decision = self.decision(result, QUERY)
        self.assertEqual(decision["decidedBy"], "coverage")
        self.assertEqual(decision["obligations"], ["teardown", "alwaysForLiteDB"])
        self.assertEqual(result["targets"], ["chaos", "index", "query"])
        self.assertEqual(result["missing"], ["lifetime-chaos", "teardown-faults"])

    def test_a_pattern_in_a_comment_does_not_trigger(self):
        _, _, result = self.run_select({QUERY: QUERY_FINALLY.replace("try { } finally { }", "// finally Dispose")})
        self.assertEqual(self.decision(result, QUERY)["obligations"], ["alwaysForLiteDB"])

    def test_removed_lines_count_as_changes(self):
        files = {QUERY: QUERY_FINALLY}
        _, _, result = self.run_select({QUERY: FILES[QUERY]}, files=files)
        self.assertIn("teardown", self.decision(result, QUERY)["obligations"])

    def test_an_undecided_product_file_selects_every_known_target(self):
        code, _, result = self.run_select({OTHER: "class Unmapped { int x; }\n"})
        self.assertEqual(code, 0)
        self.assertTrue(result["allTargets"])
        self.assertEqual(self.decision(result, OTHER)["decidedBy"], "fallback-all")
        self.assertEqual(result["targets"], ["chaos", "index", "query", "shared"])
        self.assertEqual(result["missing"], ["lifetime-chaos", "teardown-faults"])
        self.assertNotIn("deadline-selftest", result["targets"])

    def test_documentation_is_ignored_and_selects_nothing(self):
        code, _, result = self.run_select({"docs/guide.md": "# Changed\n", "README.md": "x\n"})
        self.assertEqual(code, 0)
        self.assertEqual({item["decidedBy"] for item in result["decisions"]}, {"ignored"})
        self.assertEqual(self.decision(result, "docs/guide.md")["obligations"], ["ignore:docs/**"])
        self.assertEqual(result["targets"], [])
        self.assertEqual(result["missing"], [])

    def test_an_unknown_non_product_file_falls_back_to_all_targets(self):
        _, _, result = self.run_select({"build/settings.props": "<Project />\n"})
        self.assertTrue(result["allTargets"])

    def test_a_target_helper_selects_the_targets_that_use_it(self):
        _, _, result = self.run_select({HELPER: FILES[HELPER].replace("{ }", "{ int seed; }")})
        decision = self.decision(result, HELPER)
        self.assertEqual(decision["decidedBy"], "obligation")
        self.assertEqual(result["targets"], ["query"])

    def test_a_target_file_selects_itself(self):
        path = "LiteDB.Fuzz/Targets/IndexFuzzer.cs"
        _, _, result = self.run_select({path: target("IndexFuzzer", "index", "    int steps;\n")})
        self.assertEqual(result["targets"], ["index"])

    def test_seed_is_fixed_per_pr_and_written_to_github_output(self):
        github_output = self.scratch / "github_output"
        os.environ["GITHUB_OUTPUT"] = str(github_output)
        try:
            _, _, result = self.run_select({OTHER: "class Unmapped { int y; }\n"}, extra=["--pr", "3080", "--github-output"])
        finally:
            del os.environ["GITHUB_OUTPUT"]
        self.assertEqual(result["seed"], 2947000 + 3080)
        lines = github_output.read_text().splitlines()
        self.assertIn("seed=2950080", lines)
        self.assertIn("targets=chaos,index,query,shared", lines)
        self.assertIn("all-targets=true", lines)
        self.assertIn("missing=lifetime-chaos,teardown-faults", lines)

    def test_available_list_overrides_the_declared_targets(self):
        _, _, result = self.run_select({SHARED: "class SharedEngine { int z; }\n"},
                                       extra=["--available", "shared,lifetime-chaos"])
        self.assertEqual(result["targets"], ["lifetime-chaos", "shared"])
        self.assertEqual(result["missing"], [])

    def test_changed_files_without_a_diff_apply_pattern_obligations_conservatively(self):
        listing = self.scratch / "changed.txt"
        listing.write_text(f"{QUERY}\n")
        _, _, result = self.run_select({}, extra=["--changed-files", str(listing)])
        decision = self.decision(result, QUERY)
        self.assertIn("teardown", decision["obligations"])
        self.assertFalse(decision["patternsEvaluated"])

    def test_changed_files_with_a_diff_evaluate_patterns(self):
        listing = self.scratch / "changed.txt"
        listing.write_text(f"{QUERY}\n")
        diff = self.scratch / "change.diff"
        diff.write_text(f"diff --git a/{QUERY} b/{QUERY}\n--- a/{QUERY}\n+++ b/{QUERY}\n@@ -4 +4 @@\n-    {{\n+    {{ Plan();\n")
        _, _, result = self.run_select({}, extra=["--changed-files", str(listing)], diff=str(diff))
        self.assertEqual(self.decision(result, QUERY)["obligations"], ["alwaysForLiteDB"])
        self.assertIsNone(result["base"])


class DiffTests(unittest.TestCase):
    def test_parse_diff_collects_added_and_removed_lines_of_new_and_deleted_files(self):
        text = ("diff --git a/x.cs b/x.cs\nnew file mode 100644\n--- /dev/null\n+++ b/x.cs\n@@ -0,0 +1,2 @@\n+a\n+--- b\n"
                "diff --git a/y.cs b/y.cs\ndeleted file mode 100644\n--- a/y.cs\n+++ /dev/null\n@@ -1 +0,0 @@\n-c\n")
        self.assertEqual(select.parse_diff(text), {"x.cs": ["a", "--- b"], "y.cs": ["c"]})


class ValidationTests(unittest.TestCase):
    def validate(self, document, files=None):
        with GitRepo() as repo:
            repo.commit({**FILES, OBLIGATIONS: document, COVERAGE: COVERAGE_MAP, **(files or {})})
            return run_quietly(select.main, ["--validate"])

    def test_the_fixture_map_is_valid(self):
        code, output = self.validate(policy())
        self.assertEqual(code, 0, output)

    def test_unknown_target_fails(self):
        document = policy(obligations=[obligation("shared-mode", ["shraed"], ["LiteDB/Client/Shared/**"])])
        code, output = self.validate(document)
        self.assertEqual(code, 1)
        self.assertIn("unknown target 'shraed'", output)

    def test_self_test_targets_are_not_known(self):
        document = policy(obligations=[obligation("selftest", ["deadline-selftest"], ["LiteDB/Client/Shared/**"])])
        self.assertEqual(self.validate(document)[0], 1)

    def test_duplicate_ids_bad_regex_unknown_kind_and_key_fail(self):
        document = policy(obligations=[
            obligation("shared-mode", ["shared"], ["LiteDB/Client/Shared/**"]),
            obligation("shared-mode", ["shared"], ["LiteDB/Client/Shared/**"], kind="sharde"),
            obligation("Bad_Id", ["shared"], ["LiteDB/**"], patterns=["(unclosed"], pattern=["x"]),
        ])
        code, output = self.validate(document)
        self.assertEqual(code, 1)
        for text in ("used twice", "kind must be one of", "kebab-case", "does not compile", "unknown key 'pattern'"):
            self.assertIn(text, output)

    def test_a_glob_that_matches_nothing_fails_unless_pending(self):
        document = policy(obligations=[obligation("typo", ["shared"], ["LiteDB/Client/Shraed/**"])])
        code, output = self.validate(document)
        self.assertEqual(code, 1)
        self.assertIn("matches no file", output)
        pending = policy(pendingPaths=["LiteDB/Client/Shraed/**"],
                         obligations=[obligation("typo", ["shared"], ["LiteDB/Client/Shraed/**"])])
        self.assertEqual(self.validate(pending)[0], 0)

    def test_ignore_must_not_hide_product_code(self):
        code, output = self.validate(policy(ignore=["**/*.cs"]))
        self.assertEqual(code, 1)
        self.assertIn("never ignored", output)

    def test_unknown_always_target_and_missing_description_fail(self):
        code, output = self.validate(policy(alwaysForLiteDB=["lifetime-chaso"], description=""))
        self.assertEqual(code, 1)
        self.assertIn("unknown target 'lifetime-chaso'", output)
        self.assertIn("description", output)

    def test_a_pending_target_that_now_exists_is_a_warning(self):
        code, output = self.validate(policy(pendingTargets=["lifetime-chaos", "teardown-faults", "chaos"]))
        self.assertEqual(code, 0, output)
        self.assertIn("pending target chaos now exists", output)

    def test_a_stale_coverage_map_is_a_warning(self):
        stale = json.dumps({"schemaVersion": 1, "targets": {"gone": ["LiteDB/Gone.cs"]}})
        code, output = self.validate(policy(), files={COVERAGE: stale})
        self.assertEqual(code, 0, output)
        self.assertIn("targets no longer known: gone", output)
        self.assertIn("1 recorded files no longer exist", output)

    def test_the_repository_map_is_valid(self):
        code, output = run_quietly(select.main, ["--validate"])
        self.assertEqual(code, 0, output)


if __name__ == "__main__":
    unittest.main()
