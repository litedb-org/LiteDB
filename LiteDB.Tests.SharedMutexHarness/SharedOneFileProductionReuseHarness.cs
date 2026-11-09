using LiteDB;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using LiteDB.Utils;

internal static class SharedOneFileProductionReuseHarness
{
    internal static bool TryRun(string mode, string filename, string[] args)
    {
        if (mode != "one-file-production-reuse-cut" && mode != "one-file-dimensions-hold") return false;
        var settings = new EngineSettings
        { Filename = filename, SharedOneFile = true, Password = args[3] == "-" ? null : args[3] };
        if (mode == "one-file-production-reuse-cut")
        {
            void Cut(string family, string stage)
            {
                if (family == "PublicationStage" && stage == "open-begin" &&
                    (args[4] == "PublicationStage:fast-admission-disabled" || args[4] == "PublicationStage:writer-uncertain"))
                    throw new IOException("Drive the production recovery failure fence");
                if (family + ":" + stage != args[4]) return;
                if (SharedMemoryAuthority.BootstrapReuseCount != 1 || SharedMemoryAuthority.BootstrapResetCount != 0)
                    throw new Exception("Production cut missed reuse");
                if (!Reachability.Snapshot().ContainsKey("fault-point:" + family))
                    throw new Exception("Production cut missed its fault marker");
                Console.WriteLine("ready");
                Console.ReadLine();
                throw new Exception("Child must be killed at production reuse cut");
            }
            SharedMemoryAuthority.BootstrapStage = stage => Cut("BootstrapStage", stage);
            SharedMemoryReaderTable.PublicationStage = stage => Cut("PublicationStage", stage);
            using var engine = new SharedEngine(settings);
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            if (db.GetCollection("docs").Count() != 160) throw new Exception("Lost production data");
            return true;
        }
        settings.SharedProcessCapacity = 3;
        settings.SharedGenerationCapacity = 3;
        using var owner = new SharedEngine(settings);
        using var database = new LiteDatabase(owner, disposeOnClose: false);
        using var anchor = owner.Query("docs", new Query());
        if (!anchor.Read()) throw new Exception("Missing live generation");
        if (args[4] == "writer")
        {
            database.BeginTrans();
            database.GetCollection("docs").Update(new BsonDocument
                { ["_id"] = 1, ["value"] = 99, ["payload"] = new string('x', 1024) });
        }
        Console.WriteLine("ready");
        Console.ReadLine();
        if (args[4] == "writer") database.Rollback();
        var count = 1;
        while (anchor.Read()) count++;
        if (count != 160) throw new Exception("Truncated streamed generation");
        Console.WriteLine("done");
        return true;
    }
}
