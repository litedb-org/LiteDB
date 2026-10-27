using LiteDB;
using LiteDB.Client.Shared;

internal static class SharedOneFileHarness
{
    internal static bool TryRun(string mode, string filename, string[] args)
    {
        if (SharedOneFileWarmHarness.TryRun(mode, filename, args)) return true;
        if (mode == "one-file-mutation-cut")
        {
            using var engine = new SharedEngine(new LiteDB.Engine.EngineSettings
            { Filename = filename, SharedOneFile = true, TransactionPageLimit = 1 });
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.CheckpointSize = 0;
            if (args[4] == "version-reset") db.GetCollection("docs").Update(new BsonDocument
                { ["_id"] = 1, ["value"] = 1, ["payload"] = new string('x', 1024) });
            else db.BeginTrans();
            SharedMemoryReaderTable.PublicationStage = stage =>
            {
                if (stage != args[4]) return;
                Console.WriteLine("ready");
                Console.ReadLine();
            };
            try
            {
                if (args[4] == "version-reset") db.Checkpoint();
                else
                    for (var revision = 1; revision < 5; revision++)
                        for (var id = 1; id <= 160; id++) db.GetCollection("docs").Update(new BsonDocument
                            { ["_id"] = id, ["value"] = revision, ["payload"] = new string('x', 1024) });
            }
            finally { SharedMemoryReaderTable.PublicationStage = null; }
            return true;
        }
        if (mode == "one-file-allocation-cut")
        {
            using var engine = new SharedEngine(new LiteDB.Engine.EngineSettings
            { Filename = filename, SharedOneFile = true, Password = args[3] == "-" ? null : args[3],
                TransactionPageLimit = 1, CompactStorage = CompactStorageMode.Legacy });
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.BeginTrans();
            for (var id = 400; id < 460; id++) db.GetCollection("abort").Insert(new BsonDocument
            { ["_id"] = id, ["value"] = 9, ["payload"] = new string('x', 2500) });
            Console.WriteLine("ready");
            Console.ReadLine();
            db.Rollback();
            return true;
        }
        if (mode == "one-file-commit-cut")
        {
            using var engine = new SharedEngine(new LiteDB.Engine.EngineSettings { Filename = filename, SharedOneFile = true });
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.BeginTrans();
            SharedMemoryReaderTable.PublicationStage = stage =>
            {
                if (stage != args[4]) return;
                Console.WriteLine("ready");
                Console.ReadLine();
            };
            try
            {
                db.GetCollection("docs").Update(new BsonDocument { ["_id"] = 1, ["value"] = 1, ["payload"] = new string('x', 1024) });
                db.Commit();
            }
            finally { SharedMemoryReaderTable.PublicationStage = null; }
            return true;
        }
        if (mode == "one-file-review-attach")
        {
            SharedMemoryAuthority.BootstrapStage = stage =>
            {
                if (stage != "before-storage") return;
                Console.WriteLine("attached");
                Console.ReadLine();
            };
            LiteDB.Engine.EngineState.SimulateProcessCrash = stage =>
            {
                if (stage != "startup-before-tail-trim") return;
                using var probe = new SharedRangeLocks(SharedMemoryAuthority.PathFor(filename));
                try
                {
                    probe.EnterWrite();
                    Console.WriteLine("WRITE-free");
                    probe.ExitWrite();
                }
                catch (IOException error) when (error.Message == "Shared main mutex / WRITE ownership mismatch.")
                { Console.WriteLine("WRITE-held"); }
            };
            try
            {
                using var engine = new SharedEngine(new LiteDB.Engine.EngineSettings { Filename = filename, SharedOneFile = true });
            }
            finally
            {
                SharedMemoryAuthority.BootstrapStage = null;
                LiteDB.Engine.EngineState.SimulateProcessCrash = null;
            }
            Console.WriteLine("done");
            return true;
        }
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
            Action<string> pause = stage =>
            {
                if (stage != args[4]) return;
                Console.WriteLine("ready");
                Console.ReadLine();
            };
            SharedMemoryReaderTable.PublicationStage = pause;
            LiteDB.Engine.EngineState.SimulateProcessCrash = pause;
            try
            {
                using var engine = new SharedEngine(new LiteDB.Engine.EngineSettings
                { Filename = filename, SharedOneFile = true, Password = args[3] == "-" ? null : args[3] });
                using var db = new LiteDatabase(engine, disposeOnClose: false);
                db.GetCollection("docs").Update(new BsonDocument { ["_id"] = 1, ["value"] = 1, ["payload"] = new string('x', 1024) });
            }
            finally
            {
                SharedMemoryReaderTable.PublicationStage = null;
                LiteDB.Engine.EngineState.SimulateProcessCrash = null;
            }
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
