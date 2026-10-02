import json
import tempfile
import unittest
from pathlib import Path

import generate_fuzz_coverage_map as generate
from safety_fixtures import GitRepo, run_quietly

ROOT = "/home/runner/work/LiteDB/LiteDB"


def coverage_xml(root, covered, uncovered, fuzz_file):
    """A dotnet-coverage XML report: LiteDB.Fuzz.dll and LiteDB.dll modules with per-module source ids."""
    def module(name, sources, ranges):
        files = "\n".join(f'        <source_file id="{index}" path="{path}" checksum_type="SHA256" checksum="00" />'
                          for index, path in enumerate(sources))
        body = "\n".join(f'            <range source_id="{source}" start_line="1" end_line="1" start_column="1" '
                         f'end_column="2" covered="{state}" />' for source, state in ranges)
        return (f'    <module id="1" name="{name}" path="{name}">\n      <functions>\n'
                f'        <function id="1" token="0x1" name="F()" namespace="N" type_name="T">\n'
                f'          <ranges>\n{body}\n          </ranges>\n        </function>\n      </functions>\n'
                f'      <source_files>\n{files}\n      </source_files>\n    </module>')
    sources = [f"{root}/{path}" for path in covered + uncovered]
    states = [(index, "yes" if index == 0 else "partial") for index in range(len(covered))]
    states += [(len(covered) + index, "no") for index in range(len(uncovered))]
    fuzz = module("LiteDB.Fuzz.dll", [f"{root}/{fuzz_file}"], [(0, "yes")])
    return ('\ufeff<?xml version="1.0" encoding="utf-8"?>\n<results>\n  <modules>\n'
            + fuzz + "\n" + module("LiteDB.dll", sources, states) + "\n  </modules>\n</results>\n")


def write_run(root, name, target, covered, uncovered=(), sha="a" * 40, finished="2026-10-02T05:00:00Z"):
    directory = Path(root) / name
    directory.mkdir(parents=True)
    (directory / "run.json").write_text(json.dumps({"target": target, "gitSha": sha, "finishedUtc": finished}))
    (directory / "coverage.xml").write_text(
        coverage_xml(ROOT, list(covered), list(uncovered), "LiteDB.Fuzz/Targets/QueryFuzzer.cs"), encoding="utf-8")


FILES = {
    "LiteDB/Engine/Query/QueryExecutor.cs": "class A { }\n",
    "LiteDB/Engine/Disk/DiskService.cs": "class B { }\n",
    "LiteDB/Document/BsonValue.cs": "class C { }\n",
    "LiteDB.Fuzz/Targets/QueryFuzzer.cs": 'class QueryFuzzer { public string Name => "query"; }\n',
    "LiteDB.Fuzz/Targets/WalFuzzer.cs": 'class WalFuzzer { public string Name => "wal"; }\n',
    ".github/safety/fuzz-obligations.json": json.dumps({"pendingTargets": ["lifetime-chaos"]}),
}


class CoverageMapTests(unittest.TestCase):
    def test_maps_each_target_to_its_covered_product_files(self):
        artifacts = tempfile.mkdtemp(prefix="fuzz-coverage-")
        write_run(artifacts, "20261002-query-s1", "query", ["LiteDB/Engine/Query/QueryExecutor.cs", "LiteDB/Document/BsonValue.cs"],
                  ["LiteDB/Engine/Disk/DiskService.cs"])
        write_run(artifacts, "20261002-query-s2", "query", ["LiteDB/Engine/Disk/DiskService.cs", "obj/Generated.g.cs"])
        write_run(artifacts, "nested/20261002-wal-s1", "wal", ["LiteDB/Engine/Disk/DiskService.cs"],
                  finished="2026-10-03T01:00:00Z")
        (Path(artifacts) / "corpus-without-coverage").mkdir()
        (Path(artifacts) / "corpus-without-coverage" / "run.json").write_text(json.dumps({"target": "wal"}))
        with GitRepo() as repo:
            repo.commit(FILES)
            output = Path(artifacts) / "map.json"
            argv = [artifacts, "--command", "dotnet run ... --coverage-guided", "--output", str(output)]
            code, text = run_quietly(generate.main, argv)
            self.assertEqual(code, 0, text)
            first = output.read_bytes()
            run_quietly(generate.main, argv)
            self.assertEqual(output.read_bytes(), first, "regenerating from the same runs is byte-identical")
        document = json.loads(first)
        self.assertEqual(document["targets"], {
            "query": ["LiteDB/Document/BsonValue.cs", "LiteDB/Engine/Disk/DiskService.cs", "LiteDB/Engine/Query/QueryExecutor.cs"],
            "wal": ["LiteDB/Engine/Disk/DiskService.cs"]})
        source = document["generatedFrom"]
        self.assertEqual(source["commit"], "a" * 40)
        self.assertEqual(source["date"], "2026-10-03")
        self.assertEqual(source["runs"], 3)
        self.assertEqual(source["notCovered"], ["lifetime-chaos"])
        self.assertIn("lifetime-chaos", text)

    def test_no_runs_is_an_error(self):
        with GitRepo() as repo:
            repo.commit(FILES)
            code, text = run_quietly(generate.main, [tempfile.mkdtemp(), "--command", "x", "--output", "/dev/null"])
        self.assertEqual(code, 1)
        self.assertIn("no run directory", text)

    def test_paths_from_another_machine_resolve_by_tracked_suffix(self):
        resolver = generate.PathResolver(["LiteDB/Engine/Disk/DiskService.cs"])
        self.assertEqual(resolver.resolve("D:\\a\\LiteDB\\LiteDB\\LiteDB\\Engine\\Disk\\DiskService.cs"),
                         "LiteDB/Engine/Disk/DiskService.cs")
        self.assertIsNone(resolver.resolve("/tmp/obj/Generated.g.cs"))


if __name__ == "__main__":
    unittest.main()
