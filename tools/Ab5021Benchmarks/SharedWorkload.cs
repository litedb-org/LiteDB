using LiteDB;

namespace Ab5021Benchmarks;

// Report-only, uncontended one-process Shared costs; no existing gate workloads change.
internal sealed class SharedWorkload
{
    private readonly string _config;
    private readonly string _root;
    private readonly bool _record;
    public List<Sample> Samples { get; } = new();

    public SharedWorkload(string config, string root, bool record)
    {
        _config = config;
        _root = root;
        _record = record;
        Directory.CreateDirectory(root);
    }

    public void Run()
    {
        var path = Path.Combine(_root, "shared.db");
        var documents = Enumerable.Range(1, 1000).Select(id => new Document
        {
            Id = id, Key = id * 17, Value = id, Payload = new string('x', 96)
        }).ToArray();
        using (var db = Open(path))
        {
            Require(db.CheckpointSize == 1000, "default checkpoint threshold");
            Require(db.GetCollection<Document>("documents").Insert(documents) == 1000, "setup insert");
        }
        Measure("shared_open_close_20x", 20, path, () =>
        {
            for (var i = 0; i < 20; i++)
            {
                using var db = Open(path);
                Require(db.GetCollectionNames().Single() == "documents", "open names");
            }
        });
        using (var db = Open(path))
        {
            var col = db.GetCollection<Document>("documents");
            Measure("shared_read_1000", 1000, path, () =>
            {
                for (var id = 1; id <= 1000; id++)
                    Require(col.FindById(id)?.Value == id, "read value");
            });
            Require(db.BeginTrans(), "begin write");
            Measure("shared_write_1000_in_tx", 1000, path, () =>
            {
                Require(col.Insert(documents.Select(d => new Document
                {
                    Id = d.Id + 1000, Key = d.Key, Value = d.Value, Payload = d.Payload
                })) == 1000, "write count");
            });
            Measure("shared_commit_1000", 1000, path, () => Require(db.Commit(), "commit"));
        }
        using (var db = Open(path))
        {
            var docs = db.GetCollection<Document>("documents").FindAll().ToArray();
            Require(docs.Length == 2000 && docs.Sum(d => (long)d.Id) == 2001000, "reopen checksum");
            Require(docs.All(d => d.Value == (d.Id - 1) % 1000 + 1 && d.Payload == new string('x', 96)),
                "reopen contents");
        }
    }

    private LiteDatabase Open(string path)
    {
        var connection = new ConnectionString { Filename = path, Connection = ConnectionType.Shared,
            Collation = new Collation(127, System.Globalization.CompareOptions.Ordinal) };
#if DEV
        Require(connection.SharedOneFile, "candidate one-file default must be enabled");
        connection.DurableCommits = _config != "dev-os-cache";
#endif
        return new LiteDatabase(connection);
    }

    private void Measure(string scenario, int operations, string path, Action action)
    {
        var measurement = new ScenarioMeasurement();
        action();
        measurement.Stop();
        if (!_record) return;
        Samples.Add(new Sample(scenario, measurement.Milliseconds, operations, new FileInfo(path).Length,
            Directory.GetFiles(_root).Sum(f => new FileInfo(f).Length), measurement.Gen0,
            measurement.Gen1, measurement.Gen2, measurement.AllocatedBytes));
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
