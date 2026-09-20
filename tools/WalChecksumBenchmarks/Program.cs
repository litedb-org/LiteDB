using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using LiteDB;

var memory = args.Contains("--memory");
foreach (var encrypted in new[] { false, true })
foreach (var bulk in new[] { false, true })
{
    var samples = new double[5];
    for (var run = -1; run < samples.Length; run++)
    {
        var path = Path.Combine(Path.GetTempPath(), "litedb-checksum-" + Guid.NewGuid() + ".db");
        try
        {
            using var database = new LiteDatabase(new ConnectionString
            {
                Filename = memory ? ":memory:" : path, Password = encrypted ? "secret" : null
            });
            database.CheckpointSize = 0;
            var collection = database.GetCollection("docs");
            collection.Insert(new BsonDocument { ["_id"] = -1 });
            database.Checkpoint();
            var docs = Enumerable.Range(0, bulk ? 10000 : 1000).Select(id =>
                new BsonDocument { ["_id"] = id, ["value"] = new string('x', 256) }).ToArray();
            var watch = Stopwatch.StartNew();
            if (bulk) collection.InsertBulk(docs);
            else foreach (var doc in docs) collection.Insert(doc);
            watch.Stop();
            if (run >= 0) samples[run] = watch.Elapsed.TotalMilliseconds;
        }
        finally
        {
            File.Delete(path);
            File.Delete(Path.Combine(Path.GetDirectoryName(path), Path.GetFileNameWithoutExtension(path) + "-log.db"));
        }
    }
    Array.Sort(samples);
    Console.WriteLine($"memory={memory}, encrypted={encrypted}, bulk={bulk}: median {samples[2]:F2} ms [{string.Join(", ", samples.Select(x => x.ToString("F2")))}]");
}
