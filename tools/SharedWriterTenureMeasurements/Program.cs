using System.Diagnostics;
using JsonSerializer = System.Text.Json.JsonSerializer;
using LiteDB;
using LiteDB.Engine;

if (typeof(LiteEngine).GetMethod("GetMonitor", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic) != null)
    throw new InvalidOperationException("Production measurements require TestingEnabled=false.");
System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
var mode = args[0];
var path = Path.GetFullPath(args[1]);
if (mode == "seed")
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    using var seed = new LiteDatabase(path);
    seed.GetCollection("docs").Insert(Row(0));
    seed.GetCollection("docs").EnsureIndex("value");
    seed.GetCollection("sentinel").Insert(new BsonDocument { ["_id"] = 42, ["value"] = "untouched" });
    return;
}
if (mode == "verify")
{
    using var cold = new LiteDatabase(path);
    var expected = int.Parse(args[2]);
    var docs = cold.GetCollection("docs");
    if (docs.Count() != 1 || docs.FindById(1)["value"] != expected ||
        docs.Find(Query.EQ("value", expected)).Single()["payload"] != new string('t', 200) ||
        cold.GetCollection("sentinel").FindById(42)["value"] != "untouched") throw new Exception("Cold verification failed");
    return;
}
var durable = bool.Parse(args[2]);
if (mode == "newcomer")
{
    Newcomer.Run(path, durable);
    return;
}
using var engine = new SharedEngine(new EngineSettings { Filename = path, SharedOneFile = true, DurableCommits = durable });
using var db = new LiteDatabase(engine, disposeOnClose: false);
db.CheckpointSize = 0;
var collection = db.GetCollection("docs");
if (mode == "worker")
{
    Console.WriteLine("ready");
    string line;
    while ((line = Console.ReadLine()) != null && line != "close")
    {
        var value = int.Parse(line);
        var document = Row(value);
        var start = Stopwatch.GetTimestamp();
        if (!collection.Update(document)) throw new Exception("Missing row");
        Console.WriteLine(Stopwatch.GetElapsedTime(start).TotalMilliseconds * 1000);
    }
    return;
}
if (mode != "single") throw new ArgumentException(mode);
var count = int.Parse(args[3]);
for (var warmup = 0; warmup < 100; warmup++) collection.Update(Row(-warmup));
var timings = new double[count];
var total = Stopwatch.StartNew();
for (var value = 1; value <= count; value++)
{
    var document = Row(value);
    var start = Stopwatch.GetTimestamp();
    if (!collection.Update(document)) throw new Exception("Missing row");
    timings[value - 1] = Stopwatch.GetElapsedTime(start).TotalMilliseconds * 1000;
}
total.Stop();
Array.Sort(timings);
Console.WriteLine(JsonSerializer.Serialize(new { mode, durable, count, seconds = total.Elapsed.TotalSeconds,
    transactionsPerSecond = count / total.Elapsed.TotalSeconds, p50Microseconds = timings[count / 2], p95Microseconds = timings[(int)(count * .95)] }));

static BsonDocument Row(int value) => new BsonDocument { ["_id"] = 1, ["value"] = value, ["payload"] = new string('t', 200) };
