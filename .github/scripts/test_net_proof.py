import json
import shutil
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

import net_proof as net
import proof_provenance as provenance
import safety_common as common
from safety_fixtures import GitRepo, run_quietly

LEDGER = ".github/safety/net-proofs.json"
INTERFACE = "LiteDB/Client/Database/ILiteDatabase.cs"
UPSTREAM_API = "namespace LiteDB { public interface ILiteDatabase { bool BeginTrans(); } }\n"
HANDLE_API = ("namespace LiteDB { public interface ILiteTransactionProvider { "
              "ILiteTransaction BeginTransaction(); } }\n")
CAPABILITIES = {
    "legacy-transactions": {"description": "d", "grep": r"\bbool\s+BeginTrans\s*\(", "in": INTERFACE},
    "handle-api": {"description": "d", "grep": r"\bILiteTransaction\s+BeginTransaction\s*\(", "in": "LiteDB/Client/**/*.cs"},
    "smoke-adapter": {"description": "d", "file": "adapted/marker.txt"},
    "overlay-commit": {"description": "d", "file": "overlay.txt"},
}
ADAPTER = "tools/net-proofs/adapters/sample"
FORK = "someone/LiteDB"
# Fires (exit 1) where state.txt says bad, stays quiet where it says good.
FIRES_WHEN_BAD = [sys.executable, "-c",
                  "import pathlib, sys; bad = pathlib.Path('state.txt').read_text().strip() == 'bad'; "
                  "print('NET-FIRED: id=lost-ack' if bad else 'quiet'); sys.exit(1 if bad else 0)"]


def entry(**changes):
    value = {"id": "sample", "rows": [16], "level": "generic", "independence": "designed-from-invariant",
             "defect": "A sample defect", "knownBad": {"kind": "dev-commit", "commit": "a" * 40},
             "fix": {"commit": "b" * 40},
             "net": {"name": "sample-net", "evidenceClass": 1, "requires": ["legacy-transactions"],
                     "overlay": {"commits": [], "patches": [], "adapters": []}, "build": [],
                     "command": FIRES_WHEN_BAD, "timeoutSeconds": 30,
                     "expect": {"knownBad": {"fires": True, "match": r"NET-FIRED: id=(?P<id>[\w-]+)"},
                                "fix": {"fires": False}}}}
    for key, item in changes.items():
        if key.startswith("net_"):
            value["net"][key[4:]] = item
        else:
            value[key] = item
    return value


def ledger(*entries, capabilities=None):
    return json.dumps({"schemaVersion": 1, "capabilities": capabilities or CAPABILITIES, "proofs": list(entries)})


ADAPTER_FILES = {f"{ADAPTER}/adapter.json": json.dumps({"name": "sample", "target": "adapted", "requires": []}),
                 f"{ADAPTER}/README.md": "readme", f"{ADAPTER}/marker.txt": "copied"}


class ValidateTests(unittest.TestCase):
    def validate(self, *entries, files=None, capabilities=None, argv=()):
        with GitRepo() as repo:
            repo.commit({LEDGER: ledger(*entries, capabilities=capabilities), **ADAPTER_FILES, **(files or {})})
            return run_quietly(net.main, ["validate", *argv])

    def test_a_complete_entry_a_skeleton_and_a_fork_entry_are_valid(self):
        skeleton = entry(id="skeleton")
        del skeleton["net"]["command"], skeleton["net"]["expect"], skeleton["net"]["timeoutSeconds"]
        fork = entry(id="fork", knownBad={"kind": "pr-commit", "commit": "c" * 40, "pr": 133, "repository": FORK},
                     net_requires=["handle-api"])
        code, output = self.validate(entry(), skeleton, fork)
        self.assertEqual(code, 0, output)
        self.assertIn("3 net proofs", output)

    def test_invalid_entries_are_rejected(self):
        cases = {
            "not in the capability table": entry(net_requires=["teleport"]),
            "lists the capabilities the net needs": entry(net_requires=[]),
            "knownBad.commit must be a full 40-character": entry(knownBad={"kind": "dev-commit", "commit": "abc"}),
            "knownBad.kind must be one of dev-commit, pr-commit": entry(knownBad={"kind": "package", "version": "5"}),
            "must be a GitHub repository as 'owner/name'": entry(
                knownBad={"kind": "pr-commit", "commit": "c" * 40, "pr": 1, "repository": "not a repo"}),
            "fix.commit must be a full": entry(fix={"commit": "HEAD"}),
            "overlay patch tools/net-proofs/x.patch does not exist": entry(
                net_overlay={"patches": ["tools/net-proofs/x.patch"]}),
            "must be a directory directly under tools/net-proofs/adapters/": entry(
                net_overlay={"adapters": ["LiteDB.Tests/Adapters"]}),
            "has no adapter.json": entry(net_overlay={"adapters": ["tools/net-proofs/adapters/missing"]}),
            "overlay commit 'HEAD' must be a full": entry(net_overlay={"commits": ["HEAD"]}),
            "declares its hard wall-clock limit": entry(net_timeoutSeconds=None),
            "evidence class 2 needs runs >= 2": entry(net_evidenceClass=2, net_runs=1),
            "states its tolerance": entry(net_evidenceClass=3),
            "expect.knownBad.fires must be true": entry(net_expect={"knownBad": {"fires": False, "match": "x"},
                                                                    "fix": {"fires": False}}),
            "a harness-smoke entry proves no ledger row": entry(level="harness-smoke", independence="harness-smoke"),
            "independence must be": entry(independence="tuned-after-fix"),
            "a not-applicable result lists the missing capabilities": entry(results={"state": "not-applicable"}),
            "contradicts state not-applicable; only proven passes": entry(
                results={"state": "not-applicable", "missing": ["handle-api"], "passed": True}),
            "a proven result fired at known-bad": entry(results={"state": "proven", "knownBad": {"fired": True},
                                                                 "fix": {"fired": True}}),
        }
        for expected, value in cases.items():
            with self.subTest(expected):
                code, output = self.validate(value)
                self.assertEqual(code, 1, output)
                self.assertIn(expected, output)

    def test_capability_probes_are_validated(self):
        for expected, spec in (("exactly one probe", {"description": "d"}),
                               ("names the files to search", {"description": "d", "grep": "x"}),
                               ("not a valid regular expression", {"description": "d", "grep": "(", "in": "*"}),
                               ("a description", {"file": "x"})):
            with self.subTest(expected):
                code, output = self.validate(entry(), capabilities={**CAPABILITIES, "broken": spec})
                self.assertEqual(code, 1, output)
                self.assertIn(expected, output)

    def test_recorded_not_applicable_results_are_never_counted_as_passing(self):
        code, output = self.validate(
            entry(results={"state": "proven", "knownBad": {"fired": True}, "fix": {"fired": False}, "passed": True}),
            entry(id="na", results={"state": "not-applicable", "missing": ["handle-api"], "passed": False}))
        self.assertEqual(code, 0, output)
        self.assertIn("1 of 2 proven; 1 not applicable and 0 not attempted (never counted as passing)", output)

    def test_summary_counts_only_proven_as_passed(self):
        summary = net.summarize(["proven", "not-applicable", "not-attempted", "harness-error"])
        self.assertEqual((summary["passed"], summary["total"]), (1, 4))
        self.assertEqual(summary["notCounted"], {"not-applicable": 1, "not-attempted": 1})


class ProvenanceTests(unittest.TestCase):
    def test_a_fork_provenance_entry_validates_through_the_forks_pull_ref(self):
        host = Path(tempfile.mkdtemp(prefix="litedb-git-host-"))
        self.addCleanup(shutil.rmtree, host, ignore_errors=True)
        with GitRepo() as repo, patch.object(provenance, "GIT_HOST", host.as_uri() + "/"):
            repo.commit({"a.txt": "1"})
            repo._git("branch", "dev")
            fork = GitRepo()
            self.addCleanup(shutil.rmtree, fork.path, ignore_errors=True)
            bad = fork.commit({"a.txt": "bad"})
            fixed = fork.commit({"a.txt": "fixed"})
            fork._git("update-ref", "refs/pull/133/head", fixed)
            (host / "someone").mkdir()
            fork.path.rename(host / "someone" / "LiteDB.git")
            value = entry(knownBad={"kind": "pr-commit", "commit": bad, "pr": 133, "repository": FORK},
                          fix={"commit": fixed})
            repo.commit({LEDGER: ledger(value)})
            code, output = run_quietly(net.main, ["validate", "--provenance", "--dev-ref", "dev"])
            self.assertEqual(code, 0, output)
            value["fix"]["commit"] = repo._git("rev-parse", "HEAD")  # not a descendant of the known-bad commit
            repo.commit({LEDGER: ledger(value)})
            code, output = run_quietly(net.main, ["validate", "--provenance", "--dev-ref", "dev"])
            self.assertEqual(code, 1, output)
            self.assertIn("does not descend from the known-bad commit", output)


class RunTests(unittest.TestCase):
    """End to end in a scratch repository: worktrees, overlay, capabilities, command, classification."""

    def setUp(self):
        self.work = Path(tempfile.mkdtemp(prefix="litedb-net-proof-work-"))
        self.addCleanup(shutil.rmtree, self.work, ignore_errors=True)

    def history(self, repo, api=UPSTREAM_API):
        bad = repo.commit({INTERFACE: api, "state.txt": "bad\n"})
        fixed = repo.commit({"state.txt": "good\n"})
        repo._git("checkout", "-q", "-b", "overlay", bad)
        overlay = repo.commit({"overlay.txt": "net harness\n"})
        repo._git("checkout", "-q", "main")
        return bad, fixed, overlay

    def run_entry(self, value, files=None, api=UPSTREAM_API, argv=()):
        with GitRepo() as repo:
            bad, fixed, overlay = self.history(repo, api)
            value = json.loads(json.dumps(value).replace("BAD", bad).replace("FIXED", fixed)
                               .replace("OVERLAY", overlay))
            repo.write({LEDGER: ledger(value), **ADAPTER_FILES, **(files or {})})
            code, output = run_quietly(net.main, ["run", "--id", value["id"], "--work-dir", str(self.work), *argv])
            summary = json.loads((self.work / "summary.json").read_text())
            result = json.loads(Path(summary["results"][0]["result"]).read_text())
            self.assertEqual(repo._git("worktree", "list").count("\n"), 0, "worktrees were not removed")
            return code, output, summary, result

    def sample(self, **changes):
        return entry(knownBad={"kind": "dev-commit", "commit": "BAD"}, fix={"commit": "FIXED"}, **changes)

    def test_a_handle_only_net_against_an_upstream_tree_is_not_applicable_and_not_a_pass(self):
        code, output, summary, result = self.run_entry(self.sample(net_requires=["handle-api"]))
        self.assertEqual(code, 1, output)
        self.assertEqual(result["state"], "not-applicable")
        self.assertEqual(result["missing"], ["handle-api"])
        self.assertEqual((result["knownBad"]["missing"], result["fix"]["missing"]), (["handle-api"], ["handle-api"]))
        self.assertIs(result["passed"], False)
        self.assertEqual((summary["passed"], summary["notCounted"]["not-applicable"]), (0, 1))
        self.assertNotIn("attempts", result["knownBad"])  # nothing was built or run
        self.assertIn("1 not applicable", output)

    def test_the_same_net_runs_where_the_handle_api_exists(self):
        code, output, _, result = self.run_entry(self.sample(net_requires=["handle-api"]), api=HANDLE_API)
        self.assertEqual((code, result["state"]), (0, "proven"), output)
        self.assertEqual(result["knownBad"]["capabilities"]["plain"], ["handle-api"])

    def test_the_overlay_is_applied_before_capabilities_are_checked(self):
        patch_text = ("diff --git a/patched.txt b/patched.txt\nnew file mode 100644\n--- /dev/null\n"
                      "+++ b/patched.txt\n@@ -0,0 +1 @@\n+patched\n")
        check = [sys.executable, "-c",
                 "import pathlib, sys; assert pathlib.Path('patched.txt').is_file(); "
                 "assert pathlib.Path('adapted/marker.txt').read_text() == 'copied'; "
                 "assert not pathlib.Path('adapted/adapter.json').exists(); "
                 "bad = pathlib.Path('state.txt').read_text().strip() == 'bad'; "
                 "print('NET-FIRED: id=x' if bad else 'quiet'); sys.exit(1 if bad else 0)"]
        value = self.sample(net_requires=["smoke-adapter", "overlay-commit"], net_command=check,
                            net_overlay={"commits": ["OVERLAY"], "patches": ["tools/net-proofs/p.patch"],
                                         "adapters": [ADAPTER]})
        code, output, _, result = self.run_entry(value, files={"tools/net-proofs/p.patch": patch_text})
        self.assertEqual((code, result["state"]), (0, "proven"), output)
        self.assertNotIn("smoke-adapter", result["knownBad"]["capabilities"]["plain"])
        self.assertIn("smoke-adapter", result["knownBad"]["capabilities"]["overlay"])
        self.assertEqual(len(result["knownBad"]["attempts"]), 2)  # class 1: the run and its replay

    def test_an_adapter_that_needs_a_missing_capability_is_not_applied(self):
        files = {f"{ADAPTER}/adapter.json": json.dumps({"target": "adapted", "requires": ["handle-api"]})}
        code, _, _, result = self.run_entry(self.sample(net_overlay={"adapters": [ADAPTER]}), files=files)
        self.assertEqual((code, result["state"], result["missing"]), (1, "not-applicable", ["handle-api"]))
        self.assertNotIn("overlay", result["knownBad"]["capabilities"])

    def test_every_other_outcome_is_classified_and_fails(self):
        quiet = [sys.executable, "-c", "print('quiet')"]
        always = [sys.executable, "-c", "import sys; print('NET-FIRED: id=x'); sys.exit(1)"]
        other = [sys.executable, "-c", "import sys; print('Unhandled exception'); sys.exit(1)"]
        # Fires with a different assertion each time: a controlled net that does not replay.
        flaky = [sys.executable, "-c", "import pathlib, sys, time; print(f'NET-FIRED: id=t{time.monotonic_ns()}'); "
                                       "sys.exit(int(pathlib.Path('state.txt').read_text().strip() == 'bad'))"]
        slow = [sys.executable, "-c", "import time; time.sleep(30)"]
        cases = {
            "not-fired": dict(net_command=quiet),
            "fired-at-fix": dict(net_command=always),
            "fired-differently": dict(net_command=other),
            "not-reproduced": dict(net_command=flaky),
            "harness-error": dict(net_command=slow, net_timeoutSeconds=1),
        }
        for state, changes in cases.items():
            with self.subTest(state):
                code, output, summary, result = self.run_entry(self.sample(**changes))
                self.assertEqual((code, result["state"]), (1, state), output)
                self.assertEqual(summary["passed"], 0)

    def test_a_conflicting_overlay_is_a_harness_error(self):
        value = self.sample(net_overlay={"patches": ["tools/net-proofs/p.patch"]})
        broken = "diff --git a/state.txt b/state.txt\n--- a/state.txt\n+++ b/state.txt\n@@ -1 +1 @@\n-other\n+x\n"
        code, _, _, result = self.run_entry(value, files={"tools/net-proofs/p.patch": broken})
        self.assertEqual((code, result["state"]), (1, "harness-error"))
        self.assertIn("git apply", result["reason"])

    def test_a_skeleton_records_capabilities_and_is_not_attempted(self):
        value = self.sample()
        del value["net"]["command"]
        code, output, summary, result = self.run_entry(value)
        self.assertEqual((code, result["state"], summary["passed"]), (0, "not-attempted", 0), output)
        self.assertEqual(result["knownBad"]["capabilities"]["overlay"], ["legacy-transactions"])

    def test_native_stress_records_the_fire_rate(self):
        once = [sys.executable, "-c",
                "import os, pathlib, sys; bad = pathlib.Path('state.txt').read_text().strip() == 'bad'; "
                "first = os.environ['NET_PROOF_ARTIFACTS'].endswith('run-0'); "
                "print('NET-FIRED: id=x' if bad and first else 'quiet'); sys.exit(1 if bad and first else 0)"]
        code, output, _, result = self.run_entry(self.sample(net_evidenceClass=2, net_runs=3, net_command=once))
        self.assertEqual((code, result["state"]), (0, "proven"), output)
        self.assertEqual((result["knownBad"]["fireRate"], result["fix"]["fireRate"]), (0.333, 0.0))

    def test_performance_nets_alternate_sides_and_apply_the_tolerance(self):
        command = [sys.executable, "-c",
                   "import pathlib, sys; bad = pathlib.Path('state.txt').read_text().strip() == 'bad'; "
                   "print('p99=' + ('9.5' if bad else '1.0')); print('NET-FIRED: id=p99' if bad else ''); "
                   "sys.exit(int(bad))"]
        value = self.sample(net_evidenceClass=3, net_runs=3, net_command=command,
                            net_tolerance={"fireFraction": 0.6, "metric": r"p99=(?P<value>[\d.]+)"})
        code, output, _, result = self.run_entry(value)
        self.assertEqual((code, result["state"]), (0, "proven"), output)
        self.assertEqual((result["knownBad"]["metricMedian"], result["fix"]["metricMedian"]), (9.5, 1.0))
        self.assertEqual([len(result[side]["attempts"]) for side in ("knownBad", "fix")], [3, 3])

    def test_overridden_commits_are_marked_as_not_ledger_evidence(self):
        code, _, _, result = self.run_entry(self.sample(), argv=("--fix", "HEAD~1"))  # the known-bad commit
        self.assertEqual((code, result["state"]), (1, "fired-at-fix"))
        self.assertEqual(result["overridden"], {"fix": "HEAD~1"})
        self.assertEqual(result["fix"]["commit"], result["knownBad"]["commit"])
        self.assertIn("not evidence for the ledger entry", result["note"])


class ProbeTests(unittest.TestCase):
    def test_upstream_obsolete_messages_do_not_count_as_a_handle_api(self):
        upstream = '[Obsolete("Use BeginTransaction() and Commit/Rollback")] bool BeginTrans();'
        self.assertEqual(net.probe(CAPABILITIES, [INTERFACE], lambda _: upstream), ["legacy-transactions"])

    def test_the_repository_ledger_names_only_probed_capabilities(self):
        report = common.Report("")
        tree = common.Tree(common.WORKTREE, cwd=str(Path(__file__).resolve().parents[2]))
        capabilities, entries = net.load(tree, report)
        net.validate_capabilities(capabilities, report)
        for value in entries:
            net.validate_entry(tree, value, capabilities, report)
        self.assertEqual(report.errors, [])
        self.assertIn("harness-smoke", [value["id"] for value in entries])


if __name__ == "__main__":
    unittest.main()
