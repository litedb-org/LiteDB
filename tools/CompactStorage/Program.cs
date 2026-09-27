using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

using LiteDB;
using LiteDB.Engine;

var label = args.ElementAtOrDefault(0) ?? "baseline";
var compact = args.ElementAtOrDefault(1) == "compact";
var count = int.Parse(args.ElementAtOrDefault(2) ?? "5000");
var repeats = int.Parse(args.ElementAtOrDefault(3) ?? "3");
var root = Path.Combine(Path.GetTempPath(), "litedb-2920-" + Guid.NewGuid());
Directory.CreateDirectory(root);
try
{
    foreach (var shape in new[] { "stable", "optional", "nested", "arrays", "dynamic", "types", "tiny", "large", "mixed" }.Where(s => args.Length < 5 || s == args[4]))
    {
        var docs = Enumerable.Range(1, count).Select(i => Make(shape, i)).ToArray();
        for (var pass = -1; pass < repeats; pass++)
        {
            var path = Path.Combine(root, shape + pass + ".db");
            var settings = new EngineSettings { Filename = path };
            // Reflection lets the identical harness measure the pre-feature assembly.
            SetCompactStorage(settings, compact && shape != "mixed");
            using var db = new LiteEngine(settings);
            db.Pragma("CHECKPOINT", 0);
            var insert = Measure(() => db.Insert("items", docs, BsonAutoId.Int32));
            if (shape == "mixed" && compact)
            {
                db.Dispose();
                SetCompactStorage(settings, true);
            }
            using var mixed = shape == "mixed" && compact ? new LiteEngine(settings) : null;
            var engine = mixed ?? db;
            engine.Pragma("CHECKPOINT", 0);
            var wal = new FileInfo(Path.ChangeExtension(path, null) + "-log.db").Length;
            engine.Checkpoint();
            var bytes = new FileInfo(path).Length;
            var updates = docs.Where((_, i) => i % 2 == 0).ToArray();
            var update = Measure(() => engine.Update("items", updates));
            var updateWal = new FileInfo(Path.ChangeExtension(path, null) + "-log.db").Length;
            engine.Checkpoint();
            var point = Measure(() =>
            {
                for (var i = 1; i <= Math.Min(count, 1000); i++)
                {
                    using var reader = engine.Query("items", new Query { Where = { BsonExpression.Create("$._id = @0", i) } });
                    if (!reader.Read()) throw new Exception("Missing document");
                }
            });
            var scan = Measure(() =>
            {
                using var reader = engine.Query("items", new Query());
                var found = 0;
                while (reader.Read()) found++;
                if (found != count) throw new Exception("Wrong count");
            });
            var disk = typeof(LiteEngine).GetField("_disk", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(engine);
            var schemaCacheBytes = disk?.GetType().GetProperty("SchemaCacheBytes", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(disk) ?? 0L;
            var afterUpdateBytes = new FileInfo(path).Length;
            var image = File.ReadAllBytes(path);
            var catalogBytes = Enumerable.Range(0, image.Length / 8192).Count(i => image[i * 8192 + 4] == 6) * 8192;
            var dataPages = Enumerable.Range(0, image.Length / 8192).Count(i => image[i * 8192 + 4] == 4);
            var rebuild = Measure(() => engine.Rebuild(new RebuildOptions()));
            if (pass >= 0) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
            {
                label, shape, count, pass, bytes, pages = bytes / 8192, wal, updateWal, afterUpdateBytes, catalogBytes, dataPages, schemaCacheBytes,
                insert, update, point, scan, rebuild, rebuiltBytes = new FileInfo(path).Length
            }));
        }
    }
}
finally
{
    Directory.Delete(root, true);
}

static void SetCompactStorage(EngineSettings settings, bool enabled)
{
    var property = typeof(EngineSettings).GetProperty("CompactStorage");
    if (property == null) return;
    var value = property.PropertyType == typeof(bool) ?
        (object)enabled : Enum.Parse(property.PropertyType, enabled ? "Compact" : "Legacy");
    property.SetValue(settings, value);
}

static object Measure(Action action)
{
    var allocated = GC.GetAllocatedBytesForCurrentThread();
    var watch = Stopwatch.StartNew();
    action();
    return new { ms = watch.Elapsed.TotalMilliseconds, allocated = GC.GetAllocatedBytesForCurrentThread() - allocated };
}

static BsonDocument Make(string shape, int id)
{
    var doc = new BsonDocument { ["_id"] = id };
    if (shape == "tiny")
    {
        doc["x"] = id;
        return doc;
    }
    for (var f = 0; f < 15; f++)
    {
        if (shape == "optional" && id % 5 != 0 && (id + f) % 5 < 2) continue;
        var key = shape == "dynamic" ? $"key_{id}_{f}" : $"PropertyName{f:D2}";
        doc[key] = shape == "types" && id % 2 == 0 ? new BsonValue("unknown") : new BsonValue(id + f);
    }
    if (shape == "nested") doc["Address"] = new BsonDocument
    {
        ["StreetName"] = "123 Main Street", ["PostalCode"] = "12345", ["CityName"] = "Example"
    };
    if (shape == "arrays") doc["Measurements"] = new BsonArray(Enumerable.Range(0, 100).Select(i => new BsonValue(i)));
    if (shape == "large") doc["Description"] = new string('x', 16000);
    return doc;
}
