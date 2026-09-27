using System.Reflection;
using LiteDB;
using LiteDB.Engine;

internal static class SharedMappedHarness
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static bool TryRun(string mode, string filename, string? password, string[] args)
    {
        if (!mode.StartsWith("mapped-", StringComparison.Ordinal)) return false;
        using var engine = new SharedEngine(new EngineSettings { Filename = filename, Password = password });
        typeof(SharedEngine).GetProperty("CoordinatedIdleLimit", Private)!.SetValue(engine, TimeSpan.FromMinutes(1));
        using var database = new LiteDatabase(engine, disposeOnClose: false);
        var rows = database.GetCollection("docs");
        for (var i = 0; i < 3; i++)
            if (rows.FindById(0)["value"].AsInt32 != 0) throw new Exception("Incorrect warm snapshot");
        var owner = typeof(SharedEngine).GetField("_owner", Private)!.GetValue(engine)!;
        owner.GetType().GetMethod("WaitForRelease")!.Invoke(owner, null);
        if (mode == "mapped-slot-half")
        {
            var registry = typeof(SharedEngine).GetField("_readers", Private)!.GetValue(engine)!;
            var slots = registry.GetType().GetField("_slots", Private)!.GetValue(registry)!;
            slots.GetType().GetProperty("WriteOverride", Private)!.SetValue(slots, (Action<FileStream, byte[]>)((stream, bytes) =>
            {
                stream.Write(bytes, 0, 4);
                Console.WriteLine("ready");
                Console.ReadLine();
                throw new IOException("A half-published lease child must be killed by its parent");
            }));
        }
        else
        {
            var stage = mode == "mapped-opening" ? "opening" : args[4];
            var hook = typeof(SharedEngine).GetField("CoordinationStage", Private)!;
            hook.SetValue(engine, (Action<string>)(point =>
            {
                if (point != stage) return;
                hook.SetValue(engine, null);
                Console.WriteLine("ready");
                Console.ReadLine();
            }));
        }
        if (mode == "mapped-opening")
            rows.Update(new BsonDocument { ["_id"] = 63, ["value"] = 99, ["payload"] = new string('x', 3000) });
        else
        {
            var result = rows.FindById(63);
            if (result["value"].AsInt32 != 7 || result["payload"].AsString != new string('x', 3000))
                throw new Exception("Mapped admission exposed the wrong snapshot");
        }
        Console.WriteLine("done");
        return true;
    }
}
