import json
import unittest

import check_fault_points as check
from safety_fixtures import GitRepo, run_quietly, csharp_class

REGISTRY = ".github/safety/fault-points.json"
ENGINE = "LiteDB/Engine/Disk/DiskService.Sample.cs"
STATE = "LiteDB/Engine/EngineState.cs"
TESTS = "LiteDB.Tests/Engine/Crash_Tests.cs"
FUZZ = "LiteDB.Fuzz/Targets/PowerLossFuzzer.cs"
WORKFLOW = ".github/workflows/fuzz.yml"

STATE_SOURCE = """namespace LiteDB.Engine
{
    internal class EngineState
    {
#if DEBUG || TESTING
        internal static Action<string> SimulateProcessCrash;
        internal Action<PageBuffer> SimulateDiskWriteFail;
        internal static Action<long> ObserveSortSpill;
#endif
        internal void CrashPoint(string phase)
        {
            SimulateProcessCrash?.Invoke(phase);
        }
    }
}
"""
ENGINE_SOURCE = """namespace LiteDB.Engine
{
    internal partial class DiskService
    {
        void Write(bool confirmed)
        {
            this.CrashPoint("wal-before-flush");
            this.CrashPoint(confirmed ? "wal-confirmation-write" : "wal-page-write");
        }
    }
}
"""
TEST_SOURCE = csharp_class("Crash_Tests", {
    "Crashes": ("Fact", 'EngineState.SimulateProcessCrash = p => { if (p == "wal-before-flush") throw new X(); };'),
    "Writes": ("Fact", "state.SimulateDiskWriteFail = page => throw new IOException();"),
})
FUZZ_SOURCE = 'class PowerLossFuzzer { public string Name => "power-loss"; string[] p = { "wal-confirmation-write", "wal-page-write" }; }'


def hook(name, evidence, **extra):
    return {"family": "process-crash", "name": name, "protocol": "WAL", "evidence": evidence, **extra}


def registry(hooks=None, injectors=None, observers=None):
    return json.dumps({
        "hooks": hooks if hooks is not None else [
            hook("wal-before-flush", [{"test": f"{TESTS}#Crashes", "model": "process-death", "proves": "p"}]),
            hook("wal-confirmation-write", [{"fuzz": "power-loss", "model": "modeled-power-loss", "proves": "p"}]),
            hook("wal-page-write", [], gap="No evidence claims this hook yet."),
        ],
        "injectors": injectors if injectors is not None else [
            {"name": "SimulateDiskWriteFail", "protocol": "WAL writes",
             "evidence": [{"test": f"{TESTS}#Writes", "model": "exception", "proves": "p"}]}],
        "observers": observers if observers is not None else {"ObserveSortSpill": "Measurement hook, not a fault."},
    })


BASE = {STATE: STATE_SOURCE, ENGINE: ENGINE_SOURCE, TESTS: TEST_SOURCE, FUZZ: FUZZ_SOURCE,
        WORKFLOW: "jobs:\n  smoke:\n    targets: power-loss,wal\n"}


class FaultPointTests(unittest.TestCase):
    def run_check(self, files, base_extra=None):
        with GitRepo() as repo:
            base = repo.commit({**BASE, REGISTRY: registry(), **(base_extra or {})})
            repo.commit(files)
            return run_quietly(check.main, ["--base", base])

    def test_registered_hooks_with_resolvable_evidence_pass(self):
        code, output = self.run_check({})
        self.assertEqual(code, 0, output)
        self.assertIn("**none**", output)  # the gap row stays visible

    def test_a_new_hook_must_be_registered(self):
        source = ENGINE_SOURCE.replace('this.CrashPoint("wal-before-flush");',
                                       'this.CrashPoint("wal-before-flush"); this.CrashPoint("wal-new");')
        code, output = self.run_check({ENGINE: source})
        self.assertEqual(code, 1)
        self.assertIn("process-crash:wal-new is not registered", output)

    def test_a_removed_hook_must_leave_the_registry(self):
        code, output = self.run_check({ENGINE: ENGINE_SOURCE.replace('this.CrashPoint("wal-before-flush");', "")})
        self.assertEqual(code, 1)
        self.assertIn("process-crash:wal-before-flush no longer exists", output)

    def test_a_hook_without_a_literal_name_is_rejected_but_forwarders_are_not(self):
        source = ENGINE_SOURCE.replace('this.CrashPoint("wal-before-flush");',
                                       'this.CrashPoint("wal-before-flush"); this.CrashPoint(name);')
        code, output = self.run_check({ENGINE: source})
        self.assertEqual(code, 1)
        self.assertEqual(output.count("ERROR: LiteDB/"), 1, output)
        self.assertIn(f"ERROR: {ENGINE}:7: Fault hook without a literal name", output)

    def test_teardown_steps_are_a_hook_family_that_must_be_registered(self):
        steps = "LiteDB/Utils/TeardownSteps.cs"
        markers = """namespace LiteDB.Utils
{
    internal static class TeardownSteps
    {
        internal static void Before(string step, bool applies = true) { }
        internal static void After(string step, bool applies = true) { }
    }
    internal class TryCatch
    {
        public void Step(string step, bool applies = true) { }
    }
}
"""
        sort = "LiteDB/Engine/Sort/SortDisk.cs"
        source = """namespace LiteDB.Engine
{
    internal class SortDisk
    {
        void Dispose(TryCatch tc)
        {
            TeardownSteps.Before("SortDisk.Dispose.pool"); _pool.Dispose(); TeardownSteps.After("SortDisk.Dispose.pool");
            tc.Step("SortDisk.Dispose.delete");
        }
    }
}
"""
        sweep = "LiteDB.Tests/Safety/Tests/TeardownSweep_Tests.cs"
        test = csharp_class("TeardownSweep_Tests", {"Sweep": ("Fact", 'var steps = new[] { "SortDisk.Dispose.pool", "SortDisk.Dispose.delete" };')})
        code, output = self.run_check({steps: markers, sort: source, sweep: test})
        self.assertEqual(code, 1)
        self.assertIn("teardown-step:SortDisk.Dispose.pool is not registered", output)
        self.assertIn("teardown-step:SortDisk.Dispose.delete is not registered", output)
        self.assertNotIn("TeardownSteps.cs", output)  # the marker declarations are not hook sites
        hooks = json.loads(registry())["hooks"] + [
            {"family": "teardown-step", "name": name, "protocol": "teardown: SortDisk.Dispose",
             "evidence": [{"test": f"{sweep}#Sweep", "model": "exception", "proves": "p"}]}
            for name in ("SortDisk.Dispose.pool", "SortDisk.Dispose.delete")]
        code, output = self.run_check({steps: markers, sort: source, sweep: test, REGISTRY: registry(hooks=hooks)})
        self.assertEqual(code, 0, output)

    def test_evidence_must_resolve_and_name_the_hook(self):
        cases = {
            "does not resolve": hook("wal-before-flush", [{"test": f"{TESTS}#Missing", "model": "exception", "proves": "p"}]),
            "never names the hook": hook("wal-before-flush", [{"test": f"{TESTS}#Writes", "model": "exception", "proves": "p"}]),
            "is not run by": hook("wal-before-flush", [{"fuzz": "power-loss", "model": "exception", "proves": "p"}]),
            "add evidence or state the gap": hook("wal-before-flush", []),
        }
        for expected, entry in cases.items():
            with self.subTest(expected):
                hooks = json.loads(registry())["hooks"]
                hooks[0] = entry
                files = {REGISTRY: registry(hooks=hooks)}
                if expected == "is not run by":
                    files[WORKFLOW] = "jobs:\n  smoke:\n    targets: wal\n"
                code, output = self.run_check(files)
                self.assertEqual(code, 1)
                self.assertIn(expected, output)

    def test_every_testing_delegate_must_be_classified(self):
        source = STATE_SOURCE.replace("#endif", "        internal static Func<string, int> SimulateErrno;\n#endif")
        code, output = self.run_check({STATE: source})
        self.assertEqual(code, 1)
        self.assertIn("Test hook SimulateErrno is not registered", output)

        code, output = self.run_check({REGISTRY: registry(observers={})})
        self.assertEqual(code, 1)
        self.assertIn("Test hook ObserveSortSpill is not registered", output)

    def test_malformed_registry_is_reported_not_raised(self):
        for content, expected in (('{"hooks": "oops"}', "'hooks' must be a JSON array"),
                                  ("{broken", "is not valid JSON")):
            with self.subTest(content):
                code, output = self.run_check({REGISTRY: content})
                self.assertEqual(code, 1)
                self.assertIn(expected, output)
                self.assertNotIn("Traceback", output)

    def test_new_persistent_io_is_flagged_for_review(self):
        source = ENGINE_SOURCE.replace("void Write(bool confirmed)\n        {",
                                       "void Write(bool confirmed)\n        {\n            stream.SetLength(0);")
        code, output = self.run_check({ENGINE: source})
        self.assertEqual(code, 0, output)
        self.assertIn(f"WARNING: {ENGINE}:7: New persistent I/O operation", output)


if __name__ == "__main__":
    unittest.main()
