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
WORKFLOW = ".github/workflows/fuzz.yml"


def target(cls, name, body=""):
    return f'namespace LiteDB.Fuzz.Targets;\ninternal sealed class {cls} : IFuzzTarget\n{{\n    public string Name => "{name}";\n{body}}}\n'


WORKFLOW_TEXT = """jobs:
  smoke:
    runs-on: ${{ matrix.os }}
    strategy:
      matrix:
        include:
          - name: linux-core
            os: ubuntu-latest
            targets: query,shared,chaos
            count: 30
          # A comment between legs.
          - name: windows-persistence
            os: windows-latest
            targets: shared
            count: 10
    steps:
      - name: Run deterministic shard
        run: dotnet run -- --target "${{ matrix.targets }}"
  pr-selected:
    runs-on: ubuntu-latest
    steps:
      - name: Run selected targets
        run: echo
"""

FILES = {
    WORKFLOW: WORKFLOW_TEXT,
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


def cap(count, leg, reason="Real processes: a few rounds bound the cost."):
    return {"count": count, "smokeLeg": leg, "reason": reason}


def counts(**overrides):
    document = {"selected": 100, "allTargets": 30,
                "caps": {"shared": cap(30, "linux-core"),
                         "lifetime-chaos": cap(40, None, "Explorer programs; no Linux smoke leg in this fixture.")},
                "uncapped": {"chaos": "Single-process state machine, cheap per step."}}
    document.update(overrides)
    return document


def policy(**overrides):
    document = {
        "schemaVersion": 1,
        "description": "Obligations first, coverage second, all targets otherwise.",
        "alwaysForLiteDB": ["lifetime-chaos"],
        "pendingTargets": ["lifetime-chaos", "teardown-faults"],
        "pendingPaths": ["LiteDB.Tests/Engine/ConcurrencyExplorer/**"],
        "prCounts": counts(),
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

    def test_all_selects_every_known_target_for_a_critical_change(self):
        code, _, result = self.run_select({"docs/guide.md": "# Changed\n"}, extra=["--all"])
        self.assertEqual(code, 0)
        self.assertTrue(result["allTargets"])
        self.assertEqual(result["targets"], ["chaos", "index", "query", "shared"])
        self.assertEqual(result["reasons"]["query"], ["critical change: all targets"])
        self.assertEqual(self.decision(result, "docs/guide.md")["decidedBy"], "ignored")

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


    def test_counts_are_grouped_with_caps_below_the_selected_count(self):
        github_output = self.scratch / "github_output"
        os.environ["GITHUB_OUTPUT"] = str(github_output)
        try:
            code, output, result = self.run_select(
                {SHARED: "class SharedEngine { int w; }\n", QUERY: QUERY_FINALLY.replace("try { } finally { }", "Plan();")},
                extra=["--available", "shared,lifetime-chaos,query,index", "--github-output"])
        finally:
            del os.environ["GITHUB_OUTPUT"]
        self.assertEqual(code, 0, output)
        self.assertEqual(result["targets"], ["index", "lifetime-chaos", "query", "shared"])
        self.assertEqual(result["groups"], [{"count": 100, "targets": ["index", "query"]},
                                            {"count": 40, "targets": ["lifetime-chaos"]},
                                            {"count": 30, "targets": ["shared"]}])
        self.assertIn("count-groups=100:index,query 40:lifetime-chaos 30:shared", github_output.read_text().splitlines())

    def test_all_targets_mode_runs_min_of_cap_and_the_all_targets_count(self):
        _, _, result = self.run_select({OTHER: "class Unmapped { int v; }\n"}, extra=["--available",
                                                                                     "chaos,index,query,shared,lifetime-chaos"])
        self.assertTrue(result["allTargets"])
        self.assertEqual(result["groups"], [{"count": 30, "targets": ["chaos", "index", "lifetime-chaos", "query", "shared"]}])
        small = policy(prCounts=counts(allTargets=20))
        _, _, result = self.run_select({OTHER: "class Unmapped { int v; }\n"}, obligations=small,
                                       extra=["--available", "index,shared"])
        self.assertEqual(result["groups"], [{"count": 20, "targets": ["index", "shared"]}])

    def test_no_selected_target_gives_no_group(self):
        _, _, result = self.run_select({"docs/guide.md": "# Changed\n"})
        self.assertEqual(result["groups"], [])


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

    def test_a_cap_that_differs_from_its_smoke_leg_fails(self):
        code, output = self.validate(policy(prCounts=counts(caps={"shared": cap(25, "linux-core")})))
        self.assertEqual(code, 1)
        self.assertIn("count 25 differs from smoke leg linux-core (count 30)", output)
        changed = WORKFLOW_TEXT.replace("count: 30", "count: 25")
        self.assertEqual(self.validate(policy(prCounts=counts(caps={"shared": cap(25, "linux-core")})),
                                       files={WORKFLOW: changed})[0], 0)

    def test_a_cap_must_name_a_leg_on_the_pr_runner_that_runs_the_target(self):
        for leg, text in (("windows-persistence", "must run shared on ubuntu-latest"),
                          ("linux-cor", "is not a leg of the smoke matrix"),
                          (None, "smoke leg linux-core runs shared on ubuntu-latest with count 30")):
            code, output = self.validate(policy(prCounts=counts(caps={"shared": cap(10, leg)})))
            self.assertEqual(code, 1, leg)
            self.assertIn(text, output)
        code, output = self.validate(policy(prCounts=counts(caps={"shared": {"count": 30, "reason": "x" * 30}})))
        self.assertIn("name the smoke leg", output)

    def test_malformed_counts_fail(self):
        document = policy(prCounts=counts(selected=0, extra=1, caps={"shraed": cap(5, None, "short")},
                                          uncapped={"chaos": ""}))
        code, output = self.validate(document)
        self.assertEqual(code, 1)
        for text in ("prCounts.selected must be a positive integer", "unknown key 'extra'", "unknown target 'shraed'",
                     "say why the target is capped", "say why the target runs the full count"):
            self.assertIn(text, output)
        document = json.loads(policy())
        del document["prCounts"]
        self.assertIn("'prCounts' must be an object", self.validate(json.dumps(document))[1])

    def test_a_process_or_explorer_target_needs_a_cap_decision(self):
        spawner = target("IndexFuzzer", "index", "    void Run() => Process.Start(new ProcessStartInfo(\"dotnet\"));\n")
        files = {"LiteDB.Fuzz/Targets/IndexFuzzer.cs": spawner}
        code, output = self.validate(policy(), files=files)
        self.assertEqual(code, 1)
        self.assertIn("target index starts child processes or drives the concurrency explorer", output)
        uncapped = counts(uncapped={"index": "Its child process only verifies a file, cheap per step."})
        self.assertEqual(self.validate(policy(prCounts=uncapped), files=files)[0], 0)
        commented = target("IndexFuzzer", "index", "    // Process.Start is not used here.\n")
        self.assertEqual(self.validate(policy(), files={"LiteDB.Fuzz/Targets/IndexFuzzer.cs": commented})[0], 0)

    def test_smoke_legs_ignore_steps_and_read_the_pr_runner(self):
        legs, runner = select.smoke_legs(WORKFLOW_TEXT)
        self.assertEqual(runner, "ubuntu-latest")
        self.assertEqual(legs, {"linux-core": {"os": "ubuntu-latest", "targets": ["query", "shared", "chaos"], "count": 30},
                                "windows-persistence": {"os": "windows-latest", "targets": ["shared"], "count": 10}})

    def test_the_repository_map_is_valid(self):
        code, output = run_quietly(select.main, ["--validate"])
        self.assertEqual(code, 0, output)


if __name__ == "__main__":
    unittest.main()
