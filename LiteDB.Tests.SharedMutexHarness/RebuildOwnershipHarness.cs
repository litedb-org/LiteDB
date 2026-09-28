using System.Reflection;
using LiteDB;
using LiteDB.Engine;

internal static class RebuildOwnershipHarness
{
    internal static bool TryRun(string mode, string filename, string? password, string[] args)
    {
        if (!mode.StartsWith("rebuild-", StringComparison.Ordinal)) return false;
        var settings = new EngineSettings { Filename = filename, Password = password };
        if (mode == "rebuild-waiting-open")
        {
            settings.ReadOnly = true;
            typeof(EngineSettings).GetProperty("BeforeOpeningAdmission", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(settings, (Action)(() => Console.WriteLine("admitting")));
            using (var db = new LiteDatabase(new LiteEngine(settings)))
            {
                if (db.GetCollection("rows").Count() != 2 ||
                    db.GetCollection("rows").FindById(2)["value"] != "wal")
                    throw new InvalidOperationException("Read an incomplete replacement");
            }
            Console.WriteLine("opened");
            return true;
        }
        if (mode == "rebuild-probe")
        {
            var admission = typeof(LiteDatabase).Assembly.GetType("LiteDB.Engine.RebuildAdmission")!;
            try
            {
                using var admitted = (IDisposable?)admission.GetMethod("Enter", BindingFlags.Static | BindingFlags.NonPublic)!
                    .Invoke(null, new object[] { settings, 0 });
                throw new InvalidOperationException("Competing admission passed the recovery owner");
            }
            catch (TargetInvocationException ex) when (ex.InnerException is IOException) { }
            if (args[4] == "physical")
            {
                try
                {
                    using var stream = new FileStream(filename, FileMode.Open, FileAccess.ReadWrite,
                        FileShare.ReadWrite | FileShare.Delete);
                    throw new InvalidOperationException("Competing file handle passed the recovery owner");
                }
                catch (IOException) { }
            }
            Console.WriteLine("done");
            return true;
        }
        if (mode == "rebuild-write")
        {
            using (var db = new LiteDatabase(new LiteEngine(settings)))
                db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 4, ["value"] = "contender" });
            Console.WriteLine("done");
            return true;
        }
        if (mode == "rebuild-no-locks")
        {
            using var db = new LiteDatabase(new LiteEngine(settings));
            try { db.Rebuild(); }
            catch (PlatformNotSupportedException) { Console.WriteLine("done"); return true; }
            throw new InvalidOperationException("Rebuild accepted ineffective file-sharing locks");
        }
        if (mode != "rebuild-ownership") throw new ArgumentException(mode);
        var service = typeof(LiteDatabase).Assembly.GetType("LiteDB.Engine.RebuildService")!;
        var hook = service.GetField("SimulateInstallFailure", BindingFlags.Static | BindingFlags.NonPublic)!;
        var ownershipHook = service.GetField("SimulateOwnershipFailure", BindingFlags.Static | BindingFlags.NonPublic)!;
        Action<string> pause = point =>
        {
            if (point != args[4]) return;
            Console.WriteLine("ready");
            if (Console.ReadLine() != "continue") throw new InvalidOperationException("Missing resume command");
        };
        hook.SetValue(null, pause);
        ownershipHook.SetValue(null, pause);
        try
        {
            using var db = new LiteDatabase(new LiteEngine(settings));
            db.Rebuild();
        }
        finally { hook.SetValue(null, null); ownershipHook.SetValue(null, null); }
        Console.WriteLine("done");
        return true;
    }
}
