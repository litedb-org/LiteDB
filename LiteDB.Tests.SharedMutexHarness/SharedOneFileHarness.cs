using LiteDB;
using LiteDB.Client.Shared;

internal static class SharedOneFileHarness
{
    internal static bool TryRun(string mode, string filename, string[] args)
    {
        if (mode == "one-file-bootstrap-cut")
        {
            var settings = new LiteDB.Engine.EngineSettings { Filename = filename, SharedMode = true };
            using var admission = NativeAdmission.Open(settings, true);
            SharedMemoryAuthority.BootstrapStage = stage =>
            {
                if (stage != args[4]) return;
                Console.WriteLine("ready");
                Console.ReadLine();
            };
            using var authority = SharedMemoryAuthority.Open(settings, admission.Identity, () =>
            {
                using var mutex = SharedMutexFactory.Create(SharedMutexNameFactory.Create(filename, settings.SharedMutexNameStrategy));
                try { mutex.WaitOne(); } catch (System.Threading.AbandonedMutexException) { }
                try
                {
                    settings.SharedAdmission = admission;
                    using var core = new LiteDB.Engine.LiteEngine(settings);
                    using var db = new LiteDB.LiteDatabase(core, disposeOnClose: false);
                    if (db.GetCollection("docs").FindById(1)["value"].AsString != "acknowledged") throw new Exception("Lost commit");
                }
                finally { mutex.ReleaseMutex(); }
            });
            return true;
        }
        if (mode != "one-file-lock-hold") return false;
        using var locks = new SharedRangeLocks(filename);
        if (!locks.TryExclusiveDms()) throw new Exception("DMS busy");
        locks.DowngradeDms();
        if (!locks.TryClaim(0)) throw new Exception("Slot busy");
        locks.EnterWrite();
        Console.WriteLine("ready");
        Console.ReadLine();
        Console.WriteLine("done");
        return true;
    }
}
