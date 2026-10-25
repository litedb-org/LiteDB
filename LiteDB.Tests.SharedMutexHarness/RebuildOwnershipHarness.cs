#pragma warning disable LITEDB_EXPERIMENTAL_COORDINATOR
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
        if (mode == "rebuild-waiting-write")
        {
            // args[4]: direct, shared or coordinated. Announces its first admission, then writes.
            var announced = 0;
            typeof(EngineSettings).GetProperty("BeforeOpeningAdmission", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(settings, (Action)(() =>
                {
                    if (Interlocked.Exchange(ref announced, 1) == 0) Console.WriteLine("admitting");
                }));
            ILiteEngine engine = args[4] == "direct" ? new LiteEngine(settings) :
                args[4] == "shared" ? new SharedEngine(settings) : new CoordinatedEngine(settings);
            using (var db = new LiteDatabase(engine))
            {
                var rows = db.GetCollection("rows");
                if (rows.Count() != 2 || rows.FindById(2)["value"] != "wal" ||
                    db.GetCollection("unrelated").FindById(1)["value"] != "preserved")
                    throw new InvalidOperationException("Read an incomplete replacement");
                rows.Insert(new BsonDocument { ["_id"] = 4, ["value"] = "contender" });
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
        if (mode != "rebuild-ownership" && mode != "rebuild-service-ownership") throw new ArgumentException(mode);
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
            if (mode == "rebuild-service-ownership")
            {
                // The rebuild used by opening recovery: no engine stays open afterwards.
                var rebuilder = Activator.CreateInstance(service, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, new object[] { settings }, null)!;
                service.GetMethod("Rebuild")!.Invoke(rebuilder, new object?[] { new RebuildOptions(), null });
            }
            else
            {
                using var db = new LiteDatabase(new LiteEngine(settings));
                db.Rebuild();
            }
        }
        finally { hook.SetValue(null, null); ownershipHook.SetValue(null, null); }
        Console.WriteLine("done");
        return true;
    }
}
