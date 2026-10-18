using System.Collections.Concurrent;
using System.Reflection;
using LiteDB;
using LiteDB.Engine;
using LiteDB.ReproRunner.Shared;
using LiteDB.ReproRunner.Shared.Messaging;

namespace Issue_3097_SortDiskDispose;

internal static class Program
{
    private static int Main()
    {
        var host = ReproHostClient.CreateDefault();
        ReproConfigurationReporter.SendConfiguration(host);
        var context = ReproContext.FromEnvironment();
        var directory = Path.Combine(context.SharedDatabaseRoot ?? Path.GetTempPath(),
            "Issue_3097_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var reproduced = Reproduce(Path.Combine(directory, "sort.db"));
            var marker = reproduced ? "ISSUE_3097_REPRODUCED" : "ISSUE_3097_FIXED";
            Console.WriteLine(marker);
            host.SendResult(reproduced, marker);
            return reproduced ? 0 : 1;
        }
        catch (Exception error)
        {
            // Setup, reflection and integrity failures must never count as a fixed result.
            host.SendResult(false, "ISSUE_3097_HARNESS_ERROR", new { Exception = error.ToString() });
            Console.Error.WriteLine(error);
            return 2;
        }
        finally
        {
            foreach (var file in Directory.GetFiles(directory)) File.Delete(file);
            Directory.Delete(directory);
        }
    }

    private static bool Reproduce(string filename)
    {
        const int count = 1100;
        var scratch = Path.Combine(Path.GetDirectoryName(filename)!, "sort-tmp.db");
        var documents = Enumerable.Range(1, count).Select(id => new BsonDocument
        {
            ["_id"] = id,
            ["key"] = (id * 7919 % count).ToString("D6") + new string('k', 760)
        }).ToArray();
        bool reproduced;
        using (var engine = new LiteEngine(new EngineSettings { Filename = filename }))
        using (var db = new LiteDatabase(engine, disposeOnClose: false))
        {
            db.GetCollection("big").InsertBulk(documents);
            using var reader = db.GetCollection("big").Query().OrderBy("key").ToEnumerable().GetEnumerator();
            if (!reader.MoveNext() || !File.Exists(scratch) || new FileInfo(scratch).Length == 0)
                throw new InvalidOperationException("The sorted query did not spill to a real scratch file.");

            // Return the query's leased reader before engine close. On Windows an outstanding
            // reader independently blocks File.Delete, even without an injected close failure.
            reader.Dispose();

            // No production fault hook is needed. Match the issue's injection into the idle reader pool.
            var sort = Field(engine, "_sortDisk");
            var pool = Field(sort, "_pool");
            var streams = (ConcurrentBag<Stream>)Field(pool, "_pool");
            var fault = new ThrowingCloseStream(scratch);
            streams.Add(fault);
            engine.Dispose();
            if (!fault.FaultReached) throw new InvalidOperationException("The close fault was not reached.");
            reproduced = File.Exists(scratch);
        }

        // A leaked scratch file is the only permitted difference; all committed records must survive.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var reopened = new LiteDatabase(filename);
            var actual = reopened.GetCollection("big").FindAll().OrderBy(x => x["_id"].AsInt32).ToArray();
            if (actual.Length != documents.Length || actual.Where((doc, i) =>
                doc["_id"] != documents[i]["_id"] || doc["key"] != documents[i]["key"]).Any())
                throw new InvalidOperationException("Committed documents changed after reopening.");
        }
        return reproduced;
    }

    private static object Field(object instance, string name) =>
        instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(instance)
        ?? throw new InvalidOperationException($"Required field {name} was not found.");

    private sealed class ThrowingCloseStream : FileStream
    {
        public bool FaultReached { get; private set; }

        public ThrowingCloseStream(string filename)
            : base(filename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite) { }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                FaultReached = true;
                throw new IOException("Forced scratch stream close failure");
            }
        }
    }
}
