using System.Reflection;
using LiteDB;
using LiteDB.Engine;
using LiteDB.ReproRunner.Shared;
using LiteDB.ReproRunner.Shared.Messaging;

namespace Issue_3059_ReadOnlyStorageNoLog;

internal static class Program
{
    private static int Main()
    {
        var host = ReproHostClient.CreateDefault();
        ReproConfigurationReporter.SendConfiguration(host);
        var context = ReproContext.FromEnvironment();
        var directory = Path.Combine(context.SharedDatabaseRoot ?? Path.GetTempPath(),
            "Issue_3059_ReadOnlyStorageNoLog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var assembly = typeof(LiteDatabase).Assembly;
        host.SendLog($"LiteDB {assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion} loaded from {assembly.Location}");
        assembly.GetType("LiteDB.LiteDBPragmas")?.GetMethod("I_AM_AWARE_MY_DATABASE_BREAKS_WHEN_I_USE_THIS")?.Invoke(null, null);
        try
        {
            var reproduced = Reproduce(Path.Combine(directory, "repro.db"));
            host.SendResult(reproduced, reproduced
                ? "REPRODUCED: the acknowledged commit disappeared after closing a non-writable caller data stream without a log."
                : "FIXED: the write was refused before acknowledgement; the original rows and bytes survived reopen.");
            return reproduced ? 0 : 1;
        }
        catch (Exception error)
        {
            host.SendResult(false, "INCONCLUSIVE: " + error.Message, new { Exception = error.ToString() });
            Console.Error.WriteLine(error);
            return 2;
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch (IOException) { }
        }
    }

    private static bool Reproduce(string path)
    {
        using (var seed = new LiteDatabase(path))
        {
            seed.GetCollection("rows").EnsureIndex("value");
            seed.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "keep" });
        }
        var original = File.ReadAllBytes(path);
        var refused = false;
        using (var data = new FileStream(path, FileMode.Open, FileAccess.Read))
        {
            using (var db = new LiteDatabase(new LiteEngine(new EngineSettings { DataStream = data })))
            {
                Require(db.BeginTrans(), "explicit transaction did not start");
                try
                {
                    db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 2, ["value"] = "lost" });
                }
                catch (IOException error) when (error.Message.Contains("read-only"))
                {
                    refused = true;
                }
                Require(db.Commit(), "transaction did not complete");
                Require(db.GetCollection("rows").Count() == (refused ? 1 : 2), "unexpected live row count");
            }
            Require(data.CanRead, "caller-owned stream was disposed");
        }
        Require(File.ReadAllBytes(path).SequenceEqual(original), "data bytes changed");
        using var reopened = new LiteDatabase(path);
        Require(reopened.GetCollection("rows").FindAll().Select(row => row["_id"].AsInt32).SequenceEqual(new[] { 1 }),
            "reopened rows differ from the original committed state");
        Require(reopened.GetCollection("rows").Find(Query.EQ("value", "keep")).Single()["_id"] == 1,
            "original indexed row changed");
        return !refused;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
