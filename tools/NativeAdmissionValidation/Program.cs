using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using LiteDB;

// Black-box production-assembly check. /a and /b must be two directory bind
// mounts of the same complete database namespace, inside one mutex namespace.
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            var engineState = typeof(LiteDatabase).Assembly.GetType("LiteDB.Engine.EngineState");
            Require(engineState != null && engineState.GetField("SimulateDataWriteFail", BindingFlags.Instance | BindingFlags.NonPublic) == null,
                "The validation runner must load a production assembly without test hooks.");
            var runtime = Environment.GetEnvironmentVariable("LITEDB_EXPECTED_RUNTIME_MAJOR");
            var architecture = Environment.GetEnvironmentVariable("LITEDB_EXPECTED_ARCHITECTURE");
            if (runtime != null) Require(Environment.Version.Major.ToString() == runtime, "Wrong runtime.");
            if (architecture != null) Require(RuntimeInformation.ProcessArchitecture.ToString().Equals(architecture,
                StringComparison.OrdinalIgnoreCase), "Wrong architecture.");
            if (args[0] == "scenario") await Scenario(args[1], args[2]);
            else if (args[0] == "probe") Probe(args[1], args[2] == "readonly", args[3] == "reject");
            else if (args[0] == "write") Write(args[1]);
            else if (args[0] == "verify") Verify(args[1]);
            else throw new ArgumentException("Unknown command.");
            Console.WriteLine("passed:" + args[0]);
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static async Task Scenario(string root, string alias)
    {
        var file = Path.Combine(root, "data.db");
        var alternate = Path.Combine(alias, "data.db");
        using (var seed = new LiteDatabase(file))
        {
            seed.GetCollection("rows").Insert(new[] { Row(1, 10), Row(2, 20) });
            seed.GetCollection("rows").EnsureIndex("value", unique: true);
            seed.GetCollection("unrelated").Insert(Row(7, 700));
            seed.FileStorage.Upload("asset", "original.bin", new MemoryStream(new byte[] { 0 }));
        }
        Require(File.Exists(alternate), "Alias mount did not expose the seeded database.");
        using (var first = Open(file, shared: true, readOnly: true))
        {
            Require(first.GetCollection("rows").Count() == 2, "Initial reader did not run.");
            var data = StorageBytes(root);
            Probe(alternate, directReadOnly: false, reject: true);
            await Child("probe", alternate, "shared", "reject");
            var after = StorageBytes(root);
            Require(data.Count == after.Count && data.All(pair => after.TryGetValue(pair.Key, out var bytes) &&
                bytes.SequenceEqual(pair.Value)), "Rejected alias changed the data/WAL pair.");
            await Child("probe", file, "shared", "allow");
            await Child("write", file);
            Require(first.GetCollection("rows").FindById(1)["value"].AsInt32 == 11, "Idle retained reader missed commit.");
            for (var cycle = 0; cycle < 2; cycle++)
            {
                using var writer = Open(file, shared: true);
                writer.Rebuild(); // First owner was read-only: transfer fd and then inode.
                Require(first.GetCollection("rows").FindById(3)["value"].AsInt32 == 30, "Retained reader missed replacement.");
                await Child("probe", alternate, "shared", "reject");
                await Child("probe", file, "shared", "allow");
                Console.WriteLine("replacement:" + (cycle + 1));
            }
        }
        using (var first = Open(file, shared: false, readOnly: true))
        {
            Require(first.GetCollection("rows").Count() == 2, "Read-only Direct owner did not run.");
            Probe(alternate, directReadOnly: true, reject: true);
            await Child("probe", alternate, "readonly", "reject");
            await Child("probe", file, "readonly", "allow");
        }
        await Child("verify", file);
        using (var next = new LiteDatabase(file))
        {
            next.GetCollection("rows").Insert(Row(4, 40));
            Require(next.GetCollection("rows").Delete(4), "Post-recovery mutation made no progress.");
            next.Checkpoint();
        }
        await Child("verify", file);
        Require(!Directory.GetFiles(root, "*-shared-mode").Any(), "Persistent admission sidecar was created.");
        Console.WriteLine($"verified:{RuntimeInformation.FrameworkDescription}:{RuntimeInformation.ProcessArchitecture}");
    }

    private static void Probe(string file, bool directReadOnly, bool reject)
    {
        try
        {
            using var db = Open(file, shared: !directReadOnly, readOnly: directReadOnly);
            Require(db.GetCollection("rows").Count() == 2, "Probe did not read the committed rows.");
        }
        catch (DatabaseAdmissionException error) when (reject)
        {
            Require(error.Message.Contains("different canonical path", StringComparison.Ordinal), "Wrong rejection: " + error);
            return;
        }
        Require(!reject, "UNSAFE_ALIAS_ADMITTED: incompatible canonical storage paths shared admission.");
    }

    private static void Write(string file)
    {
        using var db = Open(file, shared: true);
        var rows = db.GetCollection("rows");
        Require(db.BeginTrans(), "Transaction did not begin.");
        Require(rows.Update(Row(1, 11)), "Update missed existing row.");
        Require(rows.Delete(2), "Delete missed existing row.");
        rows.Insert(Row(3, 30));
        db.FileStorage.Upload("asset", "committed.bin", new MemoryStream(new byte[] { 1, 2, 3 }));
        Require(db.Commit(), "Transaction was not acknowledged.");
        Require(db.BeginTrans(), "Rollback transaction did not begin.");
        rows.Update(Row(1, -1));
        rows.Insert(Row(99, 99));
        db.FileStorage.Upload("asset", "aborted.bin", new MemoryStream(new byte[] { 9 }));
        Require(db.Rollback(), "Transaction did not roll back.");
        Console.WriteLine("acknowledged:update-delete-insert-blob;aborted:update-insert-blob");
    }

    private static void Verify(string file)
    {
        using var db = new LiteDatabase(file);
        var rows = db.GetCollection("rows");
        var actual = rows.FindAll().OrderBy(x => x["_id"].AsInt32).ToArray();
        Require(actual.Length == 2 && actual.All(x => x.Count == 2 && x["_id"].IsInt32 && x["value"].IsInt32) &&
            actual[0].Equals(Row(1, 11)) && actual[1].Equals(Row(3, 30)), "Cold typed rows differ from acknowledged history.");
        var plan = rows.Query().Where("value = 11").GetPlan()["index"];
        Require(plan["name"].AsString == "value" && plan["mode"].AsString.StartsWith("INDEX SEEK", StringComparison.Ordinal),
            "Index seek path did not execute.");
        Require(rows.Find("value = 11").Single()["_id"].AsInt32 == 1, "Updated index key missing.");
        Require(!rows.Find("value = 10 OR value = 20 OR value = -1 OR value = 99").Any(), "Deleted or aborted index key survived.");
        try { rows.Insert(Row(5, 30)); throw new InvalidOperationException("Unique index was lost."); }
        catch (LiteException error) when (error.ErrorCode == LiteException.INDEX_DUPLICATE_KEY) { }
        Require(db.GetCollection("unrelated").FindAll().Single().Equals(Row(7, 700)), "Unrelated collection changed.");
        using var output = new MemoryStream();
        db.FileStorage.Download("asset", output);
        Require(output.ToArray().SequenceEqual(new byte[] { 1, 2, 3 }), "Cold FileStorage bytes differ from committed history.");
        Require(db.FileStorage.FindById("asset").Filename == "committed.bin", "Aborted FileStorage metadata survived.");
    }

    private static LiteDatabase Open(string file, bool shared, bool readOnly = false) => new LiteDatabase(new ConnectionString
        { Filename = file, Connection = shared ? ConnectionType.Shared : ConnectionType.Direct, ReadOnly = readOnly });

    private static BsonDocument Row(int id, int value) => new BsonDocument { ["_id"] = id, ["value"] = value };

    private static Dictionary<string, byte[]> StorageBytes(string root) => Directory.GetFiles(root)
        .Where(path => Path.GetFileName(path) == "data.db" || Path.GetFileName(path) == "data-log.db")
        .ToDictionary(Path.GetFileName, File.ReadAllBytes);

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private static async Task Child(params string[] arguments)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath)
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--fx-version");
        start.ArgumentList.Add(Environment.Version.ToString());
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var child = Process.Start(start);
        var output = child.StandardOutput.ReadToEndAsync();
        var error = child.StandardError.ReadToEndAsync();
        try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
        catch { if (!child.HasExited) child.Kill(entireProcessTree: true); throw; }
        Require(child.ExitCode == 0, await error + await output);
        Console.Write(await output);
    }
}
