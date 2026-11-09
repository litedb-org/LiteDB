using LiteDB;

namespace Ab5021Benchmarks;

// Diagnostic workloads only; never enter gate measurements or claim timing gains.
internal sealed partial class Workload
{
    internal void Profile(string scenario)
    {
        Console.WriteLine($"PROFILE {scenario} {_config}");
#if DEV
        if (scenario.StartsWith("durable_insert_"))
        {
            for (var i = 0; i < 4; i++)
            {
                var root = Path.Combine(_root, i.ToString());
                Directory.CreateDirectory(root);
                _ = new DurableWriters(_config, root, _documents.Take(_small).ToArray(), false).Run().ToArray();
            }
            return;
        }
#endif
        var path = Path.Combine(_root, "profile.db");
        using (var db = Open(path))
        {
            var col = db.GetCollection<Document>("documents");
            col.EnsureIndex(x => x.Key, true);
            col.Insert(_documents);
            db.Checkpoint();
            if (scenario == "full_scan_3x100k")
            {
                for (var i = 0; i < 90; i++)
                {
                    long sum = 0;
                    foreach (var doc in col.Find(Query.GTE("Value", 0))) sum += doc.Id;
                    Require(sum == (long)_count * (_count + 1) / 2, "profile checksum");
                }
                return;
            }
        }
        if (scenario != "open_close_20x") throw new ArgumentException(scenario);
        for (var i = 0; i < 4000; i++)
        {
            using var db = Open(path);
            Require(db.GetCollectionNames().Single() == "documents", "profile open");
        }
    }
}
