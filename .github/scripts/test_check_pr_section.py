import json
import os
import tempfile
import unittest

import check_pr_section as section
import render_critical_sections as render
import safety_common as common
from safety_fixtures import GitRepo, run_quietly

API = "LiteDB/Client/Database/ILiteDatabase.cs"
INTERFACE = "namespace LiteDB\n{\n    public interface ILiteDatabase\n    {\n        bool BeginTrans();\n%s    }\n}\n"
MANIFEST = ".github/safety/intended-changes.json"
NET_PROOFS = ".github/safety/net-proofs.json"
SAFETY = """## Safety / regression evidence

- **Contracts and risk:** The public surface grows by one no-op member.
- **Prior states and interactions:** No stored state; callers unaffected.
- **Failure outcomes:** None change.
- **Evidence:** api-boundary campaign, run 1.
- **Coverage delta and residual concerns:** None removed.
"""
SHA = "5dd942a7367c361fadd600be4ce10aace2768b27"


class CriticalSectionTests(unittest.TestCase):
    def setUp(self):
        self.repo = GitRepo().__enter__()
        self.base = self.repo.commit({API: INTERFACE % "", "docs/guide.md": "# Guide\n\n## Ping\n\nPing answers ok.\n",
                                      ".github/safety/contracts.json": json.dumps({"contracts": []})})

    def tearDown(self):
        self.repo.__exit__(None, None, None)

    def change(self, files):
        self.head = self.repo.commit(files)

    def check(self, body, labels=()):
        handle, path = tempfile.mkstemp(suffix=".md")
        with os.fdopen(handle, "w", encoding="utf-8") as stream:
            stream.write(body)
        try:
            return run_quietly(section.main, ["--body", path, "--base", self.base, "--labels", json.dumps(list(labels))])
        finally:
            os.remove(path)

    def errors(self, output):
        return [line for line in output.splitlines() if line.startswith("ERROR:")]

    def rendered(self, **options):
        context = section.critical_context(self.base, self.head)
        return render.render(context, common.Tree(self.head), **options)

    def api_change(self, **extra):
        self.change({API: INTERFACE % "        void Ping();\n", **extra})

    def test_non_critical_change_keeps_todays_requirements(self):
        self.change({"docs/guide.md": "# Guide\n\nMore.\n\n## Ping\n\nPing answers ok.\n"})
        code, output = self.check(SAFETY)
        self.assertEqual(code, 0, output)

    def test_critical_diff_requires_the_critical_section(self):
        self.api_change()
        code, output = self.check(SAFETY)
        self.assertEqual(code, 1)
        self.assertIn("This change is critical", output)

    def test_the_label_alone_makes_a_change_critical(self):
        self.change({"docs/guide.md": "# Guide\n\nMore.\n\n## Ping\n\nPing answers ok.\n"})
        code, output = self.check(SAFETY, labels=["critical"])
        self.assertEqual(code, 1)
        self.assertIn("Critical change (label)", output)

    def test_body_rendered_from_the_artifacts_passes(self):
        manifest = {"changes": [{"call": "Ping", "change": "outcome-change", "before": "absent", "after": "ok",
                                 "doc": "docs/guide.md#ping", "reason": "A new no-op member answers ok."}]}
        self.api_change(**{MANIFEST: json.dumps(manifest)})
        reachability = {"markers": {"api:ILiteDatabase.Ping": {"hits": 30, "targets": {"api-boundary": 30}}}}
        proofs = [{"knownBad": SHA, "net": "api-boundary", "seed": 2947, "failureId": "API_BOUNDARY_PING"}]
        body = SAFETY + "\n" + self.rendered(reachability=reachability, proof_rows=proofs, blast=[],
                                             no_hot_path="a no-op member on the public interface only")
        self.assertIn("| `api:ILiteDatabase.Ping` | 30 |", body)
        self.assertIn("`Ping` outcome-change", body)
        code, output = self.check(body)
        self.assertEqual(code, 0, output)

    def test_artifact_gaps_render_as_todo_lines_that_fail(self):
        self.api_change()
        code, output = self.check(SAFETY + "\n" + self.rendered())
        self.assertEqual(code, 1)
        errors = "\n".join(self.errors(output))
        self.assertIn("marker api:ILiteDatabase.Ping is declared by this diff", errors)
        self.assertIn("'Blast radius' needs its rows", errors)
        self.assertIn("'Benchmark diff' needs its rows", errors)

    def test_an_unhit_marker_fails(self):
        self.api_change()
        body = self.rendered(reachability={"markers": {}}, blast=[], no_hot_path="a no-op member, nothing waits here")
        code, output = self.check(SAFETY + "\n" + body)
        self.assertEqual(code, 1)
        self.assertIn("marker api:ILiteDatabase.Ping was never hit", output)

    def test_required_entries_cannot_be_replaced_by_a_reason(self):
        manifest = {"changes": [{"call": "Ping", "change": "outcome-change", "before": "absent", "after": "ok",
                                 "doc": "docs/guide.md#ping", "reason": "A new no-op member answers ok."}]}
        proof = {"proofs": [{"id": "row99-net", "knownBad": {"kind": "dev-commit", "commit": SHA},
                             "net": {"name": "explorer"}, "level": "generic"}]}
        self.api_change(**{MANIFEST: json.dumps(manifest), NET_PROOFS: json.dumps(proof)})
        body = SAFETY + """
## Critical change evidence

### Intended changes

None: nothing changes in behavior, trust me on this one.

### Markers declared

- `api:ILiteDatabase.Ping`: 4 hits

### Proofs

- api-boundary → seed 3 (no commit named here)

### Blast radius

- `ILiteDatabase.Ping` → no callers yet → new member

### Benchmark diff

No hot path touched: a no-op member on the public interface only.
"""
        code, output = self.check(body)
        errors = "\n".join(self.errors(output))
        self.assertEqual(code, 1)
        self.assertIn("list the intended change Ping/outcome-change", errors)
        self.assertIn("'Intended changes' cannot be 'None'", errors)
        self.assertIn("name the proof entry row99-net", errors)
        self.assertIn("a proof row names its known-bad commit", errors)
        self.assertNotIn("Markers declared", errors)
        self.assertNotIn("Blast radius", errors)

    def test_rendered_net_proof_entries_name_their_commit_and_state(self):
        proof = {"proofs": [{"id": "row99-net", "knownBad": {"kind": "dev-commit", "commit": SHA},
                             "net": {"name": "explorer"}, "level": "generic"}]}
        self.api_change(**{NET_PROOFS: json.dumps(proof)})
        text = self.rendered()
        self.assertIn(f"`{SHA[:12]}` → explorer → not run yet (net proof `row99-net`, generic, not attempted)", text)


if __name__ == "__main__":
    unittest.main()
