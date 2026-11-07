using LiteDB;
using LiteDB.Client.Shared;
using LiteDB.Engine;

internal static class SharedOneFileReuseHarness
{
    internal static bool TryRun(string mode, string filename, string[] args)
    {
        if (mode == "one-file-authority-hold")
        {
            var settings = new EngineSettings
            { Filename = filename, SharedMode = true, SharedProcessCapacity = args.Length > 4 ? int.Parse(args[4]) : 1, SharedGenerationCapacity = 3 };
            using var admission = NativeAdmission.Open(settings, true);
            using var authority = SharedMemoryAuthority.Open(settings, admission.Identity, () => { });
            Console.WriteLine("ready");
            Console.ReadLine();
            Console.WriteLine("done");
            return true;
        }
        if (mode != "one-file-reuse-cut") return false;
        void Pause()
        {
            if (SharedMemoryAuthority.BootstrapReuseCount != 1 || SharedMemoryAuthority.BootstrapResetCount != 0)
                throw new Exception("Death cut did not reach reuse");
            if (!LiteDB.Utils.Reachability.Snapshot().ContainsKey("fault-point:BootstrapStage"))
                throw new Exception("Missing bootstrap reachability marker");
            Console.WriteLine("ready");
            Console.ReadLine();
            throw new Exception("Reuse child must be killed");
        }
        SharedMemoryAuthority.BootstrapStage = stage => { if (stage == args[4]) Pause(); };
        EngineState.SimulateProcessCrash = stage => { if (stage == args[4]) Pause(); };
        var options = new EngineSettings { Filename = filename, SharedMode = true };
        using var native = NativeAdmission.Open(options, true);
        using var map = SharedMemoryAuthority.Open(options, native.Identity, () =>
        {
            using var mutex = SharedMutexFactory.Create(SharedMutexNameFactory.Create(filename, options.SharedMutexNameStrategy));
            try { mutex.WaitOne(); } catch (System.Threading.AbandonedMutexException) { }
            try
            {
                options.SharedAdmission = native;
                using var core = new LiteEngine(options);
                using var db = new LiteDatabase(core, disposeOnClose: false);
                if (args[4] == "validating") Pause();
                if (db.GetCollection("docs").FindById(1)["value"].AsString != "acknowledged")
                    throw new Exception("Lost committed data");
            }
            finally { mutex.ReleaseMutex(); }
        });
        return true;
    }
}
