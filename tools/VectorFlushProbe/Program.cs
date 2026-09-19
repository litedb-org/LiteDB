using System;
using System.Diagnostics;
using System.IO;
using LiteDB;
using LiteDB.Engine;

namespace VectorFlushProbe
{
    internal static class Program
    {
        private static void Main()
        {
            Console.WriteLine("mode,phase,transactions,milliseconds,data_durable_flushes,wal_durable_flushes");
            foreach (var password in new[] { null, "probe-password" })
            {
                var directory = Path.Combine(Path.GetTempPath(), "litedb-flush-probe-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                try
                {
                    Run(directory, password);
                }
                finally
                {
                    Directory.Delete(directory, true);
                }
            }
        }

        private static void Run(string directory, string password)
        {
            using var data = new CountingFileStream(Path.Combine(directory, "data.db"));
            using var log = new CountingFileStream(Path.Combine(directory, "log.db"));
            using var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log, Password = password });
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.CheckpointSize = 0;
            var docs = db.GetCollection("docs");
            // Warm the stream pools before observing per-transaction behavior.
            for (var i = 1; i <= 10; i++) docs.Insert(new BsonDocument { ["_id"] = i });
            db.Checkpoint();
            var mode = password == null ? "plain" : "encrypted";
            Measure("ordinary-commits", 1000, () =>
            {
                for (var i = 11; i <= 1010; i++) docs.Insert(new BsonDocument { ["_id"] = i });
            });
            Measure("checkpoint", 0, () => db.Checkpoint());
            db.BeginTrans();
            Measure("vector-promotion-before-commit", 0, () => docs.Insert(new BsonDocument
            {
                ["_id"] = 1011, ["vector"] = new BsonVector(new[] { 1f, 0f })
            }));
            db.Rollback();

            void Measure(string phase, int transactions, Action action)
            {
                var dataBefore = data.DurableFlushes;
                var logBefore = log.DurableFlushes;
                var elapsed = Stopwatch.StartNew();
                action();
                elapsed.Stop();
                Console.WriteLine(FormattableString.Invariant($"{mode},{phase},{transactions},{elapsed.Elapsed.TotalMilliseconds:F3},{data.DurableFlushes - dataBefore},{log.DurableFlushes - logBefore}"));
            }
        }

        private sealed class CountingFileStream : FileStream
        {
            internal int DurableFlushes;

            internal CountingFileStream(string filename)
                : base(filename, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite) { }

            public override void Flush(bool flushToDisk)
            {
                if (flushToDisk) DurableFlushes++;
                base.Flush(flushToDisk);
            }
        }
    }
}
