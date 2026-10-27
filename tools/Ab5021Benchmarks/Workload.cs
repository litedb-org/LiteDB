using System.Diagnostics;
using LiteDB;

namespace Ab5021Benchmarks;

internal sealed class Workload
{
    private readonly string _config;
    private readonly string _root;
    private readonly int _count;
    private readonly int _small;
    private readonly bool _record;
    private readonly Document[] _documents;
    public List<Sample> Samples { get; } = new();

    public Workload(string config, string root, int count, int small, bool record)
    {
        _config = config;
        _root = root;
        _count = count;
        _small = small;
        _record = record;
        Directory.CreateDirectory(root);
        var random = new Random(5021);
        _documents = Enumerable.Range(1, count).Select(id => new Document
        {
            Id = id, Key = id * 17, Value = random.Next(1000),
            Payload = Convert.ToHexString(Enumerable.Range(0, 48).Select(_ => (byte)random.Next(256)).ToArray())
        }).ToArray();
    }

    public void Run()
    {
        var path = Path.Combine(_root, "main.db");
        using (var db = Open(path))
        {
            Require(db.CheckpointSize == 1000, "default checkpoint threshold changed");
            var col = db.GetCollection<Document>("documents");
            col.EnsureIndex(x => x.Key, true);
            Measure("bulk_insert_100k", _count, path, () =>
            {
                Require(db.BeginTrans(), "begin bulk");
                Require(col.Insert(_documents) == _count, "bulk count");
                Require(db.Commit(), "commit bulk");
            });
            Verify(col, 0);
            var random = new Random(5021);
            var ids = Enumerable.Range(0, _count / 10).Select(_ => random.Next(1, _count + 1)).ToArray();
            long found = 0;
            Measure("indexed_point_10k", ids.Length, path, () =>
            {
                foreach (var id in ids)
                {
                    var doc = col.FindOne(Query.EQ("Key", id * 17));
                    Require(doc != null && doc.Id == id, "indexed result");
                    found += doc.Id;
                }
            });
            Require(found == ids.Sum(id => (long)id), "point checksum");
            long sum = 0;
            Measure("full_scan_3x100k", 3 * _count, path, () =>
            {
                for (var i = 0; i < 3; i++)
                    foreach (var doc in col.Find(Query.GTE("Value", 0))) sum += doc.Id;
            });
            Require(sum == 3L * _count * (_count + 1) / 2, "scan checksum");
            var updates = _documents.Take(_count / 10).Select(d => new Document
            {
                Id = d.Id, Key = d.Key, Value = d.Value + 1000, Payload = d.Payload
            }).ToArray();
            Measure("update_10k_one_tx", updates.Length, path, () =>
            {
                Require(db.BeginTrans(), "begin update");
                Require(col.Update(updates) == updates.Length, "update count");
                Require(db.Commit(), "commit update");
            });
            Require(col.Find(Query.GTE("Value", 1000)).Count() == updates.Length, "updated values");
            Measure("delete_10k_one_tx", _count / 10, path, () =>
            {
                Require(db.BeginTrans(), "begin delete");
                Require(col.DeleteMany(Query.LTE("_id", _count / 10)) == _count / 10, "delete count");
                Require(db.Commit(), "commit delete");
            });
            Verify(col, _count / 10);
            db.Checkpoint();
        }
        Measure("open_close_20x", 20, path, () =>
        {
            for (var i = 0; i < 20; i++)
            {
                using var db = Open(path);
                Require(db.GetCollectionNames().Single() == "documents", "open collection");
            }
        });
        using (var db = Open(path)) Verify(db.GetCollection<Document>("documents"), _count / 10);
        SmallTransactions();
        Checkpoint();
#if DEV
        if (_config == "dev-default")
            Samples.AddRange(new DurableWriters(_config, _root, _documents.Take(_small).ToArray(), _record).Run());
#endif
    }

    private void SmallTransactions()
    {
        var path = Path.Combine(_root, "small.db");
        using (var db = Open(path))
        {
            var col = db.GetCollection<Document>("documents");
            col.EnsureIndex(x => x.Key, true);
            Measure("single_insert_5k_tx", _small, path, () =>
            {
                foreach (var doc in _documents.Take(_small)) col.Insert(doc);
            });
            Require(col.Count() == _small, "small transaction count");
        }
        using (var db = Open(path))
        {
            var docs = db.GetCollection<Document>("documents").FindAll().ToArray();
            Require(docs.Length == _small && docs.Sum(d => (long)d.Id) == (long)_small * (_small + 1) / 2,
                "small transaction reopen");
        }
    }

    private void Checkpoint()
    {
        var path = Path.Combine(_root, "checkpoint.db");
        using (var db = Open(path))
        {
            // Only this scenario disables automatic checkpointing, equally on both libraries,
            // so the measured checkpoint always has the full insertion's WAL to copy.
            db.CheckpointSize = 0;
            var col = db.GetCollection<Document>("documents");
            col.EnsureIndex(x => x.Key, true);
            Require(db.BeginTrans(), "begin checkpoint setup");
            Require(col.Insert(_documents) == _count, "checkpoint setup count");
            Require(db.Commit(), "commit checkpoint setup");
            Measure("checkpoint_100k", _count, path, db.Checkpoint);
            Verify(col, 0);
        }
        using (var db = Open(path)) Verify(db.GetCollection<Document>("documents"), 0);
    }

    private LiteDatabase Open(string path)
    {
        var connection = new ConnectionString
        {
            Filename = path, Connection = ConnectionType.Direct,
            Collation = new Collation(127, System.Globalization.CompareOptions.Ordinal)
        };
#if DEV
        connection.DurableCommits = _config != "dev-os-cache";
#endif
        return new LiteDatabase(connection);
    }

    private void Verify(ILiteCollection<Document> col, int deleted)
    {
        var docs = col.FindAll().ToArray();
        Require(docs.Length == _count - deleted, "document count");
        Require(docs.Sum(d => (long)d.Id) == ((long)_count * (_count + 1) - (long)deleted * (deleted + 1)) / 2,
            "document checksum");
        Require(docs.All(d => d.Key == d.Id * 17 && d.Payload == _documents[d.Id - 1].Payload), "document contents");
        foreach (var id in new[] { deleted + 1, _count })
            Require(col.FindOne(Query.EQ("Key", id * 17))?.Id == id, "index after maintenance");
        if (deleted > 0) Require(col.FindById(1) == null, "deleted document absent");
    }

    private void Measure(string scenario, int operations, string path, Action action)
    {
        var watch = Stopwatch.StartNew();
        action();
        watch.Stop();
        if (!_record) return;
        var totalBytes = Directory.GetFiles(_root).Sum(file => new FileInfo(file).Length);
        Samples.Add(new Sample(scenario, watch.Elapsed.TotalMilliseconds, operations, new FileInfo(path).Length, totalBytes));
        Console.WriteLine($"{_config} {scenario}: {watch.Elapsed.TotalMilliseconds:F3} ms");
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
