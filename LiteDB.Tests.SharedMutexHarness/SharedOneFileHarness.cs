using LiteDB;
using LiteDB.Client.Shared;

internal static class SharedOneFileHarness
{
    internal static bool TryRun(string mode, string filename, string[] args)
    {
        if (mode.StartsWith("one-file-facade-", StringComparison.Ordinal))
        {
            try
            {
                using var engine = new SharedEngine(new LiteDB.Engine.EngineSettings { Filename = filename, SharedOneFile = true, Password = args[3] == "-" ? null : args[3] });
                using var db = new LiteDatabase(engine, disposeOnClose: false);
                if (mode.EndsWith("refused", StringComparison.Ordinal)) throw new Exception("Conflicting open admitted");
                if (mode.EndsWith("hold", StringComparison.Ordinal))
                {
                    db.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "acknowledged" });
                    db.GetCollection("docs").EnsureIndex("value");
                    db.BeginTrans();
                    db.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 2 });
                    Console.WriteLine("ready");
                    Console.ReadLine();
                    db.Rollback();
                }
            }
            catch (IOException) when (mode.EndsWith("refused", StringComparison.Ordinal)) { }
            Console.WriteLine("done");
            return true;
        }
        if (mode == "one-file-reader-hold")
        {
            var table = SharedMemoryReaderTable.Open(new LiteDB.Engine.EngineSettings { Filename = filename, SharedOneFile = true });
            using var facade = new SharedMemoryReaders(table);
            using var reader = facade.Register(11);
            Console.WriteLine("ready");
            Console.WriteLine(table.Authority.Slot + ":" + table.Authority.Incarnation);
            Console.ReadLine();
            Console.WriteLine("done");
            return true;
        }
        if (mode == "one-file-publication-hold")
        {
            SharedMemoryReaderTable.PublicationStage = stage =>
            {
                if (stage != args[4]) return;
                Console.WriteLine("ready");
                Console.ReadLine();
            };
            try
            {
                using var engine = new SharedEngine(new LiteDB.Engine.EngineSettings
                { Filename = filename, SharedOneFile = true, Password = args[3] == "-" ? null : args[3] });
                using var db = new LiteDatabase(engine, disposeOnClose: false);
                db.GetCollection("docs").Update(new BsonDocument { ["_id"] = 1, ["value"] = 1, ["payload"] = new string('x', 1024) });
            }
            finally { SharedMemoryReaderTable.PublicationStage = null; }
            Console.WriteLine("done");
            return true;
        }
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
