using System.Runtime.InteropServices;
using LiteDB;
using LiteDB.Client.Shared;
using LiteDB.Engine;

// CI-only: the driver supplies a bounded APFS volume and kills paused children.
internal static class SharedDarwinAllocationHarness
{
    internal const long Size = 64 * 1024 * 1024;

    internal static bool TryRun(string mode, string filename, string[] args)
    {
        if (!mode.StartsWith("darwin-allocation-", StringComparison.Ordinal)) return false;
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) throw new Exception("Requires native Darwin");
        Console.WriteLine($"runtime={Environment.Version} architecture={RuntimeInformation.ProcessArchitecture} mode={mode}");
        if (mode == "darwin-allocation-direct-seed")
        {
            using var file = new FileStream(filename, FileMode.Create, FileAccess.ReadWrite);
            var bytes = new byte[1024 * 1024];
            new Random(731).NextBytes(bytes);
            for (var offset = 0L; offset < Size; offset += bytes.Length) file.Write(bytes);
            file.Flush(true);
            return true;
        }
        if (mode == "darwin-allocation-direct-repeat")
        {
            using var file = new FileStream(filename, FileMode.Open, FileAccess.ReadWrite);
            for (var repeat = 0; repeat < 3; repeat++)
            {
                var error = SharedPlatformAllocation.Allocate(file, Size);
                Console.WriteLine($"Allocate repeat={repeat} length={file.Length} errno={error}");
                if (error != 0) throw new IOException($"Repeated allocation failed: errno={error}");
            }
            return true;
        }
        if (mode == "darwin-allocation-seed")
        {
            using var db = new LiteDatabase(filename);
            db.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "acknowledged" });
            db.GetCollection("docs").EnsureIndex("value");
            db.GetCollection("unrelated").Insert(new BsonDocument { ["_id"] = 7, ["payload"] = new string('z', 20000) });
        }
        var settings = new EngineSettings
        {
            Filename = filename, SharedMode = true, SharedMemorySize = Size,
            SharedProcessCapacity = mode == "darwin-allocation-seed" ? 2 : 3,
            SharedGenerationCapacity = 256
        };
        using var admission = NativeAdmission.Open(settings, true);
        var cut = mode == "darwin-allocation-seed" ? "claimed" : args[4];
        SharedMemoryAuthority.BootstrapStage = stage =>
        {
            Console.WriteLine("stage=" + stage);
            if (stage != cut) return;
            Console.WriteLine("ready");
            Console.Out.Flush();
            Console.ReadLine();
        };
        try
        {
            using var authority = SharedMemoryAuthority.Open(settings, admission.Identity, () =>
            {
                var coreSettings = settings.Clone();
                coreSettings.SharedAdmission = admission;
                using var core = new LiteEngine(coreSettings);
                using var db = new LiteDatabase(core, disposeOnClose: false);
                if (db.GetCollection("docs").FindById(1)["value"].AsString != "acknowledged" ||
                    db.GetCollection("docs").FindOne(Query.EQ("value", "acknowledged"))["_id"].AsInt32 != 1 ||
                    db.GetCollection("unrelated").FindById(7)["payload"].AsString != new string('z', 20000))
                    throw new Exception("Lost committed/indexed data or unrelated sentinel");
            });
            if (authority.Map.Load(SharedMemoryLayout.Control) != 1) throw new Exception("Not Ready");
        }
        finally { SharedMemoryAuthority.BootstrapStage = null; }
        Console.WriteLine("validated");
        return true;
    }
}
