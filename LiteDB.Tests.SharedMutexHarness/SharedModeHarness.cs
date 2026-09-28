using LiteDB;
using LiteDB.Engine;

internal static class SharedModeHarness
{
    internal static bool TryRun(string mode, string filename, string? password, string[] args)
    {
        if (!mode.StartsWith("mode-", StringComparison.Ordinal)) return false;
        var settings = new EngineSettings { Filename = filename, Password = password };
        if (mode == "mode-direct-hold")
        {
            using var direct = new LiteDatabase(new LiteEngine(settings));
            if (direct.GetCollection("rows").Count() != 150) throw new Exception("Seed missing");
            Console.WriteLine("ready");
            Console.ReadLine();
            return true;
        }
        if (mode == "mode-initialize")
        {
            var type = typeof(SharedEngine).Assembly.GetType("LiteDB.Client.Shared.SharedCoordinationFile")!;
            type.GetField("CreationStage", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(null, (Action<string, string>)((path, stage) =>
                {
                    if (stage != args[4]) return;
                    Console.WriteLine("ready");
                    Console.ReadLine();
                    throw new Exception("Child must be killed at native lock acquisition");
                }));
            using var engine = new SharedEngine(settings);
            using var db = new LiteDatabase(engine);
            db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 2 });
            throw new Exception("Initialization boundary was not reached");
        }
        try
        {
            if (mode == "mode-mutex-rejected") settings.SharedMutexNameStrategy = SharedMutexNameStrategy.Sha1Hash;
            using var db = new LiteDatabase(mode == "mode-mutex-rejected" ? new SharedEngine(settings) : new LiteEngine(settings));
            db.GetCollection("rows").DeleteAll();
        }
        catch (IOException error) when (error.Message.Contains("Cannot safely admit", StringComparison.Ordinal))
        {
            Console.WriteLine("done");
            return true;
        }
        throw new Exception("Conflicting writer was admitted");
    }
}
