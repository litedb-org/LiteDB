import json
import unittest

import check_coverage_regression as check
import safety_common as common
from safety_fixtures import GitRepo, run_quietly, csharp_class

LEDGER = ".github/safety/coverage-ledger.json"
TESTS = "LiteDB.Tests/Engine/Sample_Tests.cs"
BASE_TESTS = csharp_class("Sample_Tests", {
    "Keeps": ("Fact", "1.Should().Be(1); Assert.True(true);"),
    "Removed": ("Fact", "2.Should().Be(2);"),
})


def ledger(dispositions=(), quarantine=()):
    return json.dumps({"schemaVersion": 1, "quarantine": list(quarantine), "dispositions": list(dispositions)})


def disposition(identifier, kind, subjects, **extra):
    entry = {"id": identifier, "kind": kind, "subjects": subjects, "disposition": "replaced",
             "invariant": "The sample invariant stays covered.", "reason": "Replaced by a stronger test.",
             "coveredBy": [f"{TESTS}#Keeps"]}
    entry.update(extra)
    return entry


class CoverageRegressionTests(unittest.TestCase):
    def run_check(self, base_files, head_files):
        with GitRepo() as repo:
            base = repo.commit({LEDGER: ledger(), TESTS: BASE_TESTS, **base_files})
            repo.commit(head_files)
            return run_quietly(check.main, ["--base", base])

    def test_documentation_change_has_no_findings(self):
        code, output = self.run_check({}, {"docs/readme.md": "text"})
        self.assertEqual(code, 0, output)
        self.assertIn("No coverage-relevant changes", output)

    def test_removed_test_needs_a_disposition_added_by_the_same_change(self):
        removed = csharp_class("Sample_Tests", {"Keeps": ("Fact", "1.Should().Be(1); Assert.True(true);")})
        code, output = self.run_check({}, {TESTS: removed})
        self.assertEqual(code, 1)
        self.assertIn("test-removed: LiteDB.Tests.Engine.Sample_Tests.Removed", output)

        entry = disposition("remove-sample", "test-removed", ["LiteDB.Tests.Engine.Sample_Tests.Removed"])
        code, output = self.run_check({}, {TESTS: removed, LEDGER: ledger([entry])})
        self.assertEqual(code, 0, output)

    def test_a_disposition_from_an_earlier_change_does_not_approve_a_new_weakening(self):
        entry = disposition("old", "test-removed", ["LiteDB.Tests.Engine.Sample_Tests.Removed"])
        removed = csharp_class("Sample_Tests", {"Keeps": ("Fact", "1.Should().Be(1); Assert.True(true);")})
        code, output = self.run_check({LEDGER: ledger([entry])}, {TESTS: removed})
        self.assertEqual(code, 1)
        self.assertIn("has no disposition added in this change", output)

    def test_new_skip_and_conditional_attribute_are_findings(self):
        head = csharp_class("Sample_Tests", {
            "Keeps": ("Fact(Skip = \"flaky\")", "1.Should().Be(1); Assert.True(true);"),
            "Removed": ("MappedFact", "2.Should().Be(2);"),
        })
        code, output = self.run_check({}, {TESTS: head})
        self.assertEqual(code, 1)
        self.assertIn("skip-added: LiteDB.Tests.Engine.Sample_Tests.Keeps", output)
        self.assertIn("conditional-attribute: LiteDB.Tests.Engine.Sample_Tests.Removed", output)

    def test_fewer_assertions_in_a_test_file_is_a_finding(self):
        weaker = csharp_class("Sample_Tests", {"Keeps": ("Fact", "1.Should().Be(1);"), "Removed": ("Fact", "")})
        code, output = self.run_check({}, {TESTS: weaker})
        self.assertEqual(code, 1)
        self.assertIn(f"assertions-reduced: {TESTS} (3 -> 1 assertion calls)", output)

    def test_changed_fixture_is_a_finding_but_a_new_fixture_is_not(self):
        fixture = "LiteDB.Tests/Resources/Old_5_0_21.zip"
        code, output = self.run_check({fixture: b"old"}, {fixture: b"new", "LiteDB.Tests/Resources/New.zip": b"x"})
        self.assertEqual(code, 1)
        self.assertIn(f"fixture-changed: {fixture}", output)
        self.assertNotIn("New.zip", output.split("Detail")[0])

    def test_corpus_repin_and_new_expected_failure_are_findings(self):
        corpus = "LiteDB.Fuzz/Corpus/regressions.json"
        known = "LiteDB.Fuzz/Corpus/known-findings.json"
        case = {"target": "wal", "seed": 1, "count": 2, "inputHash": "A", "traceHash": "B"}
        base = {corpus: json.dumps({"cases": [case]}), known: json.dumps({"findings": []})}
        head = {corpus: json.dumps({"cases": [{**case, "traceHash": "C"}]}),
                known: json.dumps({"findings": [{"target": "wal", "fingerprint": "F", "status": "expected"}]})}
        code, output = self.run_check(base, head)
        self.assertEqual(code, 1)
        self.assertIn("corpus-repinned: wal:1:2", output)
        self.assertIn("expected-failure-added: wal:F", output)

    def test_removing_injector_evidence_is_a_finding(self):
        registry = ".github/safety/fault-points.json"
        evidence = [{"test": f"{TESTS}#Keeps", "model": "exception", "proves": "p"}]
        base = {registry: json.dumps({"hooks": [], "injectors": [{"name": "SimulateDiskWriteFail", "evidence": evidence}]})}
        head = {registry: json.dumps({"hooks": [], "injectors": [{"name": "SimulateDiskWriteFail", "evidence": [],
                                                                  "gap": "No longer claimed by any test."}]})}
        code, output = self.run_check(base, head)
        self.assertEqual(code, 1)
        self.assertIn("fault-evidence-removed: injector:SimulateDiskWriteFail", output)

    def test_shrinking_a_regression_proof_guard_is_a_finding(self):
        proofs = ".github/safety/regression-proofs.json"
        guard = [f"{TESTS}#Keeps", f"{TESTS}#Removed"]
        base = {proofs: json.dumps({"proofs": [{"repro": "Issue_1", "permanentGuard": guard}]})}
        head = {proofs: json.dumps({"proofs": [{"repro": "Issue_1", "permanentGuard": guard[:1]}]})}
        code, output = self.run_check(base, head)
        self.assertEqual(code, 1)
        self.assertIn(f"proof-guard-removed: Issue_1 ({TESTS}#Removed)", output)

    def test_ci_timeout_change_is_a_finding_but_a_new_workflow_is_not(self):
        workflow = ".github/workflows/ci.yml"
        base = {workflow: "jobs:\n  test:\n    timeout-minutes: 40\n"}
        head = {workflow: "jobs:\n  test:\n    timeout-minutes: 90\n",
                ".github/workflows/new.yml": "jobs:\n  x:\n    timeout-minutes: 5\n"}
        code, output = self.run_check(base, head)
        self.assertEqual(code, 1)
        self.assertIn(f"ci-test-config-changed: {workflow}", output)
        self.assertNotIn("new.yml", output)

    def test_invalid_dispositions_are_rejected(self):
        removed = csharp_class("Sample_Tests", {"Keeps": ("Fact", "1.Should().Be(1); Assert.True(true);")})
        subject = ["LiteDB.Tests.Engine.Sample_Tests.Removed"]
        cases = {
            "must name where the invariant stays covered": disposition("a", "test-removed", subject, coveredBy=[]),
            "does not resolve": disposition("b", "test-removed", subject, coveredBy=[f"{TESTS}#Missing"]),
            "must link its compatibility decision": disposition("c", "test-removed", subject,
                                                                disposition="intentional-change"),
        }
        for expected, entry in cases.items():
            with self.subTest(expected):
                code, output = self.run_check({}, {TESTS: removed, LEDGER: ledger([entry])})
                self.assertEqual(code, 1)
                self.assertIn(expected, output)

    def test_malformed_ledger_is_reported_not_raised(self):
        cases = {
            json.dumps({"dispositions": "oops", "quarantine": []}): "'dispositions' must be a JSON array",
            json.dumps({"dispositions": [disposition("x", "test-removed", "not-a-list")]}): "subjects must be a JSON array",
            "{broken": "a JSON file is malformed",
        }
        for content, expected in cases.items():
            with self.subTest(expected):
                code, output = self.run_check({}, {LEDGER: content})
                self.assertEqual(code, 1)
                self.assertIn(expected, output)

    def test_quarantine_entries_must_be_complete_and_current(self):
        stale = {"test": "LiteDB.Tests.Engine.Sample_Tests.Gone", "reason": "r", "owner": "o",
                 "review": "2099-01-01", "gap": "g"}
        code, output = self.run_check({}, {LEDGER: ledger(quarantine=[stale, {"test": "X"}])})
        self.assertEqual(code, 1)
        self.assertIn("no longer exists", output)
        self.assertIn("lacks reason, owner, review, gap", output)

    def test_quarantine_needs_a_linked_issue_and_an_expiry_date(self):
        tests = {"LiteDB.Tests/Engine/Sample_Tests.cs": csharp_class("Sample_Tests", {"Kept": ("Fact", "")})}
        entry = {"test": "LiteDB.Tests.Engine.Sample_Tests.Kept", "reason": "r", "owner": "o", "gap": "g",
                 "review": "2099-01-01"}
        cases = {
            json.dumps({**entry}): "lacks issue, expires",
            json.dumps({**entry, "issue": "soon", "expires": "2099-02-01"}): "issue must link a GitHub issue",
            json.dumps({**entry, "issue": "#12", "expires": "never"}): "expires must be an ISO date",
            json.dumps({**entry, "issue": "#12", "expires": "2098-01-01"}): "must not be after the expiry date",
        }
        for item, expected in cases.items():
            with self.subTest(expected):
                code, output = self.run_check(tests, {**tests, LEDGER: ledger(quarantine=[json.loads(item)])})
                self.assertEqual(code, 1, output)
                self.assertIn(expected, output)
        for issue in ("https://github.com/litedb-org/LiteDB/issues/3034", "litedb-org/LiteDB#3034", "#3034"):
            with self.subTest(issue):
                valid = {**entry, "issue": issue, "expires": "2099-02-01"}
                code, output = self.run_check(tests, {**tests, LEDGER: ledger(quarantine=[valid])})
                self.assertEqual(code, 0, output)

    def test_an_expired_quarantine_is_reported(self):
        report = common.Report("q")
        entry = {"test": "X.Y", "reason": "r", "owner": "o", "gap": "g", "issue": "#1",
                 "review": "2020-01-01", "expires": "2020-02-01"}
        with GitRepo() as repo:
            repo.commit({"README.md": "x"})
            check.validate_quarantine(common.Tree("HEAD"), [entry], report)
        self.assertTrue(any("expired on 2020-02-01" in message for message in report.warnings), report.warnings)


if __name__ == "__main__":
    unittest.main()
