using LiteDB;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using LiteDB.Utils;

internal static class SharedOneFileMaintenanceHarness
{
    internal static bool TryRun(string mode, string filename, string[] args)
    {
        if (!mode.StartsWith("one-file-maintenance-") && mode != "one-file-final-cut" && mode != "one-file-recovery-cut") return false;
        var settings = new EngineSettings { Filename = filename, SharedOneFile = true, Password = args[3] == "-" ? null : args[3] };
        if (mode == "one-file-maintenance-legacy-reader") { settings.ReadOnly = true; settings.LegacyIndexScan = true; }
        void Cut(string family, string stage)
        {
            if (family + ":" + stage != args[4]) return;
            var marker = family == "install" || family == "checkpoint" ? stage : family == "maintenance" ? "MaintenanceStage" :
                family == "final" ? "FinalCloseStage" : "MappedRebuildStage";
            if (!Reachability.Snapshot().ContainsKey("fault-point:" + marker))
                throw new Exception("Cut missed its fault marker: " + marker);
            Console.WriteLine("ready");
            Console.ReadLine();
            throw new Exception("Child must be killed at " + args[4]);
        }
        if (mode == "one-file-recovery-cut") MappedRebuildRecovery.MappedRebuildStage = stage => Cut("recovery", stage);
        using var engine = new SharedEngine(settings);
        using var db = new LiteDatabase(engine, disposeOnClose: false);
        if (mode == "one-file-maintenance-idle" || mode == "one-file-maintenance-legacy-reader")
        {
            db.GetCollection("docs").Count();
            Console.WriteLine("ready");
            Console.ReadLine();
            Console.WriteLine("done");
            return true;
        }
        if (mode == "one-file-maintenance-peer")
        {
            engine.CoordinatedIdleLimit = TimeSpan.FromMinutes(1);
            for (var warm = 0; warm < 3; warm++) db.GetCollection("docs").FindById(1);
            var hits = engine.CoordinatedReadHits;
            using var reader = engine.Query("docs", new Query());
            if (engine.CoordinatedReadHits != hits + 1) throw new Exception("Peer did not admit a warm snapshot");
            var snapshot = engine.MappedReaderTable.ReadCache.Snapshot.Engine;
            var count = 0;
            while (count < 1 && reader.Read()) count++;
            Console.WriteLine("ready");
            Console.ReadLine();
            using (var indexed = snapshot.Query("docs", new Query { Where = { Query.EQ("value", 0) } }))
            {
                var ids = new HashSet<int>();
                while (indexed.Read())
                {
                    var row = indexed.Current;
                    if (row["value"].AsInt32 != 0 || row["payload"].AsString != new string('x', 1024) ||
                        !ids.Add(row["_id"].AsInt32)) throw new Exception("Old peer index/payload changed");
                }
                if (!ids.SetEquals(Enumerable.Range(1, 160))) throw new Exception("Old peer index lost documents");
            }
            using (var sentinel = snapshot.Query("sentinel", new Query { Where = { Query.EQ("value", 9) } }))
                if (!sentinel.Read() || sentinel.Current["_id"].AsInt32 != 42 || sentinel.Read())
                    throw new Exception("Unrelated peer index changed");
            while (reader.Read())
            {
                if (reader.Current["value"].AsInt32 != 0) throw new Exception("Peer snapshot changed");
                count++;
            }
            if (count != 160) throw new Exception("Peer snapshot truncated");
            engine.Dispose();
            Console.WriteLine("done");
            return true;
        }
        if (mode == "one-file-recovery-cut") throw new Exception("Recovery cut was not reached: " + args[4]);
        db.CheckpointSize = mode == "one-file-final-cut" ? 1000 : 0;
        db.GetCollection("docs").Update(new BsonDocument
            { ["_id"] = 1, ["value"] = 11, ["payload"] = new string('x', 1024) });
        SharedMemoryMaintenance.MaintenanceStage = stage => Cut("maintenance", stage);
        RebuildService.SimulateInstallFailure = stage =>
        {
            if (mode == "one-file-maintenance-rollback-cut")
            {
                if (stage == "after-temp-install" ||
                    (args[4] == "install:before-source-retraction" && stage == "before-log-rollback") ||
                    (args[4] == "install:before-candidate-republish" && stage == "before-source-rollback"))
                    throw new IOException("Drive bounded installation rollback");
            }
            Cut("install", stage);
        };
        MappedRebuildRecovery.MappedRebuildStage = stage => Cut("recovery", stage);
        SharedMemoryFinalClose.FinalCloseStage = stage => Cut("final", stage);
        if (mode == "one-file-final-cut") EngineState.SimulateProcessCrash = stage => Cut("checkpoint", stage);
        if (mode == "one-file-final-cut") engine.Dispose();
        else db.Rebuild();
        throw new Exception("Maintenance cut was not reached: " + args[4]);
    }
}
