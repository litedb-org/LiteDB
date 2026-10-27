using System.Collections.Concurrent;
using System.Diagnostics;
using LiteDB;

namespace Ab5021Benchmarks;

// v5 has no per-commit device-sync option. Never compare these times to v5.
internal sealed class DurableWriters
{
    private readonly string _config;
    private readonly string _root;
    private readonly Document[] _documents;
    private readonly bool _record;

    public DurableWriters(string config, string root, Document[] documents, bool record)
    {
        _config = config;
        _root = root;
        _documents = documents;
        _record = record;
    }

    public IEnumerable<Sample> Run()
    {
        foreach (var writers in new[] { 1, 4, 8 })
        {
            var path = Path.Combine(_root, $"durable-{writers}.db");
            double milliseconds;
            using (var db = Open(path))
            {
                db.GetCollection<Document>("documents").EnsureIndex(x => x.Key, true);
                using var ready = new CountdownEvent(writers);
                using var start = new ManualResetEventSlim();
                var errors = new ConcurrentQueue<Exception>();
                var threads = Enumerable.Range(0, writers).Select(worker => new Thread(() =>
                {
                    // A dedicated thread owns each transaction from Insert through commit.
                    ready.Signal();
                    start.Wait();
                    try
                    {
                        var collection = db.GetCollection<Document>("documents");
                        for (var index = worker; index < _documents.Length; index += writers)
                            collection.Insert(_documents[index]);
                    }
                    catch (Exception error)
                    {
                        errors.Enqueue(error);
                    }
                })).ToArray();
                foreach (var thread in threads) thread.Start();
                ready.Wait();
                var watch = Stopwatch.StartNew();
                start.Set();
                foreach (var thread in threads) thread.Join();
                watch.Stop();
                milliseconds = watch.Elapsed.TotalMilliseconds;
                if (!errors.IsEmpty) throw new AggregateException(errors);
                Verify(db);
            }
            // Verification and graceful reopen are untimed. This is not a power-loss test.
            using (var db = Open(path)) Verify(db);
            if (_record)
            {
                var scenario = $"durable_insert_5k_{writers}_writers";
                Console.WriteLine($"{_config} {scenario}: {milliseconds:F3} ms (v6 scaling only)");
                yield return new Sample(scenario, milliseconds, _documents.Length,
                    new FileInfo(path).Length, Directory.GetFiles(_root).Sum(file => new FileInfo(file).Length));
            }
        }
    }

    private static LiteDatabase Open(string path)
    {
        var connection = new ConnectionString
        {
            Filename = path,
            Connection = ConnectionType.Direct,
            Collation = new Collation(127, System.Globalization.CompareOptions.Ordinal)
        };
#if DEV
        connection.DurableCommits = true;
#endif
        return new LiteDatabase(connection);
    }

    private void Verify(LiteDatabase db)
    {
        var collection = db.GetCollection<Document>("documents");
        var actual = collection.FindAll().OrderBy(document => document.Id).ToArray();
        if (actual.Length != _documents.Length) throw new InvalidOperationException("durable writer count");
        for (var index = 0; index < actual.Length; index++)
        {
            var expected = _documents[index];
            var document = actual[index];
            if (document.Id != expected.Id || document.Key != expected.Key ||
                document.Value != expected.Value || document.Payload != expected.Payload ||
                collection.FindOne(Query.EQ("Key", expected.Key))?.Id != expected.Id)
                throw new InvalidOperationException("durable writer contents/index");
        }
    }
}
