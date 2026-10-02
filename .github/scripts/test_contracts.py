import json
import os
import tempfile
import unittest

import check_contracts as check
import check_pr_section as section
from safety_fixtures import GitRepo, run_quietly, csharp_class

INDEX = ".github/safety/contracts.json"
TESTS = "LiteDB.Tests/Engine/Wal_Tests.cs"
DOC = "docs/rules/data-safety.md"
BASE = {
    TESTS: csharp_class("Wal_Tests", {"Survives": ("Fact", "1.Should().Be(1);")}),
    DOC: "# Data safety\n## Required evidence\n",
    "LiteDB.Fuzz/Targets/PowerLoss.cs": 'class P { public string Name => "power-loss"; }',
    ".github/workflows/fuzz.yml": "jobs:\n  smoke:\n    targets: power-loss\n",
    ".github/workflows/ci.yml": "run: python3 scripts/test-v9-compatibility.py\n",
    "scripts/test-v9-compatibility.py": "print()",
}


def contract(**changes):
    value = {"id": "durable-ack", "title": "Acknowledged transactions survive", "docs": [f"{DOC}#required-evidence"],
             "paths": ["LiteDB/Engine/Disk/**"],
             "evidence": [{"test": f"{TESTS}#Survives", "model": "exception", "proves": "Acknowledged commits survive."},
                          {"fuzz": "power-loss", "model": "exception", "proves": "Acknowledgments are tracked."},
                          {"script": "scripts/test-v9-compatibility.py", "proves": "Actual v9 files migrate."}]}
    value.update(changes)
    return value


def index(*contracts):
    return json.dumps({"models": {"exception": "x"}, "contracts": list(contracts) or [contract()]})


class ContractIndexTests(unittest.TestCase):
    def run_check(self, files, argv=()):
        with GitRepo() as repo:
            base = repo.commit({**BASE, INDEX: index()})
            repo.commit(files)
            return run_quietly(check.main, ["--base", base, *argv])

    def test_valid_index_reports_implicated_contracts(self):
        code, output = self.run_check({"LiteDB/Engine/Disk/DiskService.cs": "class D {}"})
        self.assertEqual(code, 0, output)
        self.assertIn("`durable-ack`: Acknowledged transactions survive", output)

    def test_unrelated_change_implicates_nothing(self):
        code, output = self.run_check({"docs/notes.md": "text"})
        self.assertEqual(code, 0, output)
        self.assertIn("None of the changed paths map to a contract", output)

    def test_dangling_references_fail(self):
        cases = {
            "does not resolve": contract(evidence=[{"test": f"{TESTS}#Gone", "proves": "Something observable."}]),
            "has no heading anchor": contract(docs=[f"{DOC}#missing"]),
            "is not run by": contract(evidence=[{"fuzz": "power-loss", "proves": "Something observable."}]),
            "is not run by any workflow": contract(evidence=[{"script": "scripts/test-v9-compatibility.py",
                                                               "proves": "Something observable."}]),
            "unknown failure model": contract(evidence=[{"test": f"{TESTS}#Survives", "model": "magic",
                                                         "proves": "Something observable."}]),
        }
        for expected, value in cases.items():
            with self.subTest(expected):
                files = {INDEX: index(value)}
                if expected == "is not run by":
                    files[".github/workflows/fuzz.yml"] = "jobs:\n  smoke:\n    targets: wal\n"
                if expected == "is not run by any workflow":
                    files[".github/workflows/ci.yml"] = "run: echo\n"
                code, output = self.run_check(files)
                self.assertEqual(code, 1)
                self.assertIn(expected, output)


    def test_claims_map_doc_sentences_to_evidence_or_a_gap(self):
        doc = {DOC: "# Data safety\n## Required evidence\n\nAcknowledged commits always survive a crash.\n"}
        good = {"doc": DOC, "sentence": "commits always survive",
                "evidence": [{"test": f"{TESTS}#Survives", "proves": "Reads the commit back after the crash."}]}
        cases = {
            None: [good, {"doc": DOC, "sentence": "always survive a crash", "gap": "No device campaign exists yet."}],
            "no sentence of": [dict(good, sentence="commits never vanish")],
            "does not exist": [dict(good, doc="docs/missing.md")],
            "either evidence items or a gap": [dict(good, gap="Both given here is wrong.")],
            "say what": [dict(good, evidence=[{"test": f"{TESTS}#Survives"}])],
            "does not resolve": [dict(good, evidence=[{"test": f"{TESTS}#Gone", "proves": "Something observable."}])],
        }
        for expected, claims in cases.items():
            with self.subTest(expected):
                code, output = self.run_check({**doc, INDEX: index(contract(claims=claims))})
                self.assertEqual(code, 1 if expected else 0, output)
                if expected:
                    self.assertIn(expected, output)

BODY = """## Summary

Change.

## Safety / regression evidence

- **Contracts and risk:** durable-ack stays supported.
- **Prior states and interactions:** Existing WAL files.
- **Failure outcomes:** A failed flush stops writes.
- **Evidence:** Wal_Tests.Survives on run 123.
- **Coverage delta and residual concerns:** None removed.
"""


class PrSectionTests(unittest.TestCase):
    def run_check(self, body, changed="LiteDB/Engine/Disk/DiskService.cs"):
        with GitRepo() as repo:
            base = repo.commit({**BASE, INDEX: index()})
            repo.commit({changed: "class D {}"})
            handle, path = tempfile.mkstemp(suffix=".md")
            with os.fdopen(handle, "w", encoding="utf-8") as stream:
                stream.write(body)
            try:
                return run_quietly(section.main, ["--body", path, "--base", base])
            finally:
                os.remove(path)

    def test_complete_section_naming_the_implicated_contract_passes(self):
        code, output = self.run_check(BODY)
        self.assertEqual(code, 0, output)

    def test_missing_section_fails(self):
        code, output = self.run_check("## Summary\n\nNo safety notes.\n")
        self.assertEqual(code, 1)
        self.assertIn("has no '## Safety / regression evidence' section", output)

    def test_unedited_template_fails(self):
        with open(os.path.join(os.path.dirname(__file__), "..", "pull_request_template.md"), encoding="utf-8") as file:
            code, output = self.run_check(file.read(), changed="docs/notes.md")
        self.assertEqual(code, 1)
        errors = [line for line in output.splitlines() if line.startswith("ERROR:")]
        self.assertEqual(len(errors), 5, output)
        self.assertTrue(all("(or justify 'Not applicable:')" in line for line in errors), output)

    def test_implicated_contract_must_be_named(self):
        code, output = self.run_check(BODY.replace("durable-ack stays supported", "Nothing changes"))
        self.assertEqual(code, 1)
        self.assertIn("implicate contract 'durable-ack'", output)

    def test_justified_not_applicable_passes_when_nothing_is_implicated(self):
        body = "## Safety / regression evidence\n\n- Not applicable: documentation only, no code or test changes.\n"
        code, output = self.run_check(body, changed="docs/notes.md")
        self.assertEqual(code, 0, output)


if __name__ == "__main__":
    unittest.main()
