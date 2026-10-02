import json
import os
import tempfile
import unittest
from pathlib import Path

import check_reachability as check
from safety_fixtures import GitRepo, run_quietly

MARKERS = ".github/safety/markers.json"
FAULTS = ".github/safety/fault-points.json"
WORKFLOW = ".github/workflows/fuzz.yml"
STATE = "LiteDB/Engine/EngineState.cs"
DISK = "LiteDB/Engine/Disk/DiskService.Sample.cs"
REBUILD = "LiteDB/Engine/Services/RebuildService.cs"
ENGINE = "LiteDB/Engine/LiteEngine.cs"
API = "LiteDB/Client/Database/ILiteDatabase.cs"
TARGET = "LiteDB.Fuzz/Targets/ChaosFuzzer.cs"

STATE_SOURCE = """namespace LiteDB.Engine
{
    internal class EngineState
    {
        internal void CrashPoint(string phase)
        {
            Reachability.FaultPoint(phase);
            SimulateProcessCrash?.Invoke(phase);
        }
    }
}
"""
DISK_SOURCE = """namespace LiteDB.Engine
{
    internal partial class DiskService
    {
        void Write()
        {
            this.CrashPoint("wal-before-flush");
        }
    }
}
"""
REBUILD_SOURCE = """namespace LiteDB.Engine
{
    internal class RebuildService
    {
        void Install()
        {
            Reachability.FaultPoint("before-log-backup");
            SimulateInstallFailure?.Invoke("before-log-backup");
        }
    }
}
"""
ENGINE_SOURCE = """namespace LiteDB.Engine
{
    public class LiteEngine
    {
        void Close()
        {
            Reachability.Sometimes("maintenance:close-during-active-transaction");
        }
    }
}
"""
API_SOURCE = """namespace LiteDB
{
    public interface ILiteDatabase
    {
        bool BeginTrans();
        BsonMapper Mapper { get; }
    }
}
"""
TARGET_SOURCE = 'class ChaosFuzzer { public string Name => "chaos"; }'


def faults():
    return json.dumps({"hooks": [
        {"family": "process-crash", "name": "wal-before-flush", "protocol": "WAL",
         "evidence": [{"fuzz": "chaos", "model": "process-death", "proves": "p"}]},
        {"family": "rebuild-install", "name": "before-log-backup", "protocol": "rebuild",
         "evidence": [{"fuzz": "chaos", "model": "exception", "proves": "p"}]}],
        "injectors": [], "observers": {}})


def entry(name, **extra):
    return {"name": name, "kind": name.split(":")[0], "paths": [ENGINE], "targets": ["chaos"],
            "reason": "Close overlaps an active transaction.", **extra}


def markers(entries=None, gates=None):
    return json.dumps({"markers": entries if entries is not None else [entry("maintenance:close-during-active-transaction")],
                       "faultPointGates": gates or {}})


BASE = {STATE: STATE_SOURCE, DISK: DISK_SOURCE, REBUILD: REBUILD_SOURCE, ENGINE: ENGINE_SOURCE, API: API_SOURCE,
        TARGET: TARGET_SOURCE, FAULTS: faults(), MARKERS: markers(),
        WORKFLOW: "jobs:\n  smoke:\n    strategy:\n      matrix:\n        include:\n          - targets: chaos\n"
                  "  nightly:\n    targets: query\n"}


class ReachabilityTests(unittest.TestCase):
    def run_check(self, files, *args, base_extra=None):
        with GitRepo() as repo:
            base = repo.commit({**BASE, **(base_extra or {})})
            repo.commit(files)
            return run_quietly(check.main, ["--base", base, *args])

    def runs(self, hits, target="chaos"):
        directory = tempfile.mkdtemp(prefix="litedb-markers-")
        run = Path(directory) / "run-1"
        run.mkdir()
        (run / "markers.json").write_text(json.dumps({"target": target, "seed": 1, "hits": hits}), encoding="utf-8")
        return directory

    def test_registered_markers_and_covered_fault_sites_pass(self):
        code, output = self.run_check({})
        self.assertEqual(code, 0, output)

    def test_an_unregistered_or_non_literal_marker_fails(self):
        source = ENGINE_SOURCE.replace("Close()\n        {",
                                       'Close()\n        {\n            Reachability.Sometimes("refusal:new");\n'
                                       "            Reachability.Sometimes(name);")
        code, output = self.run_check({ENGINE: source})
        self.assertEqual(code, 1)
        self.assertIn("Marker refusal:new is not registered", output)
        self.assertIn("needs a literal marker name", output)

    def test_a_registered_marker_that_no_longer_exists_fails(self):
        code, output = self.run_check({ENGINE: ENGINE_SOURCE.replace(
            'Reachability.Sometimes("maintenance:close-during-active-transaction");', "")})
        self.assertEqual(code, 1)
        self.assertIn("no longer exists", output)

    def test_registry_entries_must_resolve(self):
        cases = {
            "fuzz target nope does not exist": entry("maintenance:close-during-active-transaction", targets=["chaos", "nope"]),
            "needs a target that the PR smoke matrix runs": entry("maintenance:close-during-active-transaction", targets=["query"]),
            "doc docs/missing.md does not exist": entry("maintenance:close-during-active-transaction", doc="docs/missing.md"),
            "gate must be smoke, or advisory with a gateReason": entry("maintenance:close-during-active-transaction", gate="advisory"),
        }
        for expected, item in cases.items():
            with self.subTest(expected):
                code, output = self.run_check({MARKERS: markers([item])})
                self.assertEqual(code, 1)
                self.assertIn(expected, output)

    def test_every_fault_hook_site_must_count_its_marker(self):
        code, output = self.run_check({REBUILD: REBUILD_SOURCE.replace('Reachability.FaultPoint("before-log-backup");', "")})
        self.assertEqual(code, 1)
        self.assertIn("Fault hook site of fault-point:before-log-backup has no marker", output)
        code, output = self.run_check({STATE: STATE_SOURCE.replace("Reachability.FaultPoint(phase);", "")})
        self.assertEqual(code, 1)
        self.assertIn("Fault hook site of fault-point:wal-before-flush has no marker", output)

    def test_a_literal_fault_point_must_name_a_registered_hook(self):
        source = REBUILD_SOURCE.replace("void Install()\n        {", 'void Install()\n        {\n            Reachability.FaultPoint("gone");')
        code, output = self.run_check({REBUILD: source})
        self.assertEqual(code, 1)
        self.assertIn("names no hook registered", output)

    def test_an_injector_needs_a_literal_marker_at_its_site(self):
        registry = json.loads(faults())
        registry["injectors"] = [{"name": "SimulateDiskWriteFail", "protocol": "WAL page writes",
                                  "evidence": [{"fuzz": "chaos", "model": "exception", "proves": "p"}]}]
        code, output = self.run_check({FAULTS: json.dumps(registry)})
        self.assertEqual(code, 1)
        self.assertIn("Injector SimulateDiskWriteFail has no Reachability.FaultPoint", output)
        marked = REBUILD_SOURCE.replace("void Install()\n        {",
                                        'void Install()\n        {\n            Reachability.FaultPoint("SimulateDiskWriteFail");')
        code, output = self.run_check({FAULTS: json.dumps(registry), REBUILD: marked})
        self.assertEqual(code, 0, output)

    def test_diff_declares_changed_paths_and_the_campaign_must_hit_them(self):
        change = {ENGINE: ENGINE_SOURCE.replace("void Close()", "void Close( )")}
        code, output = self.run_check(change, "--runs", self.runs({}))
        self.assertEqual(code, 1)
        self.assertIn("Marker maintenance:close-during-active-transaction is declared by this diff", output)
        hits = self.runs({"maintenance:close-during-active-transaction": 3})
        code, output = self.run_check(change, "--runs", hits)
        self.assertEqual(code, 0, output)

    def test_an_advisory_declared_marker_warns_instead_of_failing(self):
        advisory = markers([entry("maintenance:close-during-active-transaction", gate="advisory",
                                  gateReason="Only xUnit tests reach it so far.")])
        code, output = self.run_check({MARKERS: advisory}, "--runs", self.runs({}))
        self.assertEqual(code, 0, output)
        self.assertIn("WARNING: Marker maintenance:close-during-active-transaction is declared", output)

    def test_an_added_fault_point_is_declared_and_writes_the_report(self):
        source = DISK_SOURCE.replace('this.CrashPoint("wal-before-flush");',
                                     'this.CrashPoint("wal-before-flush");\n            this.CrashPoint("wal-new");')
        registry = json.loads(faults())
        registry["hooks"].append({"family": "process-crash", "name": "wal-new", "protocol": "WAL",
                                  "evidence": [{"fuzz": "chaos", "model": "exception", "proves": "p"}]})
        output_path = Path(tempfile.mkdtemp()) / "reachability.json"
        code, output = self.run_check({DISK: source, FAULTS: json.dumps(registry)}, "--runs",
                                      self.runs({"fault-point:wal-before-flush": 1}), "--output", str(output_path))
        self.assertEqual(code, 1)
        self.assertIn("fault-point:wal-new is declared by this diff (changed", output)
        report = json.loads(output_path.read_text(encoding="utf-8"))
        self.assertIn("fault-point:wal-new", report["declaredUnhit"])
        self.assertIn("maintenance:close-during-active-transaction", report["neverHit"])
        self.assertEqual(report["markers"]["fault-point:wal-before-flush"]["hits"], 1)

    def test_a_changed_public_api_member_needs_a_registered_api_marker(self):
        source = API_SOURCE.replace("bool BeginTrans();", "bool BeginTrans();\n        bool BeginTrans(int timeout);")
        code, output = self.run_check({API: source})
        self.assertEqual(code, 1)
        self.assertIn("Public API ILiteDatabase.BeginTrans changed without a registered api:ILiteDatabase.BeginTrans", output)

        engine = ENGINE_SOURCE.replace("void Close()\n        {",
                                       'void Close()\n        {\n            Reachability.Sometimes("api:ILiteDatabase.BeginTrans");')
        api = entry("api:ILiteDatabase.BeginTrans", reason="Drives the new BeginTrans overload.")
        files = {API: source, ENGINE: engine,
                 MARKERS: markers([entry("maintenance:close-during-active-transaction"), api])}
        code, output = self.run_check(files)
        self.assertEqual(code, 0, output)
        self.assertIn("api:ILiteDatabase.BeginTrans", output)

    def test_a_new_public_type_needs_markers_for_its_members(self):
        code, output = self.run_check({"LiteDB/Client/Handle.cs": "namespace LiteDB { public sealed class Handle "
                                       "{ public void Commit() { } internal void Hidden() { } } }"})
        self.assertEqual(code, 1)
        self.assertIn("api:Handle.Commit", output)
        self.assertNotIn("api:Handle.Hidden", output)

    def test_no_markers_json_in_the_runs_fails(self):
        code, output = self.run_check({}, "--runs", tempfile.mkdtemp())
        self.assertEqual(code, 1)
        self.assertIn("No markers.json found", output)


if __name__ == "__main__":
    unittest.main()
