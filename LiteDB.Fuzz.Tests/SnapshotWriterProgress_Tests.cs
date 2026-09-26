using System.Diagnostics;
using LiteDB.Fuzz.Targets;
using Xunit;

namespace LiteDB.Fuzz.Tests;

public sealed class SnapshotWriterProgress_Tests
{
    [Fact]
    public void Blocked_native_writer_times_out_and_reaps_writer_and_reader_without_mutating_committed_data()
    {
        var root = Path.Combine(Path.GetTempPath(), "litedb-snapshot-timeout-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var context = new FuzzContext("snapshot", 3013, 1, null, root);
            var filename = Path.Combine(root, "test.db");
            using var db = new LiteDatabase(new ConnectionString { Filename = filename, Connection = ConnectionType.Shared });
            db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = 42, ["payload"] = new byte[9000] });
            using var reader = new SnapshotFuzzer.ReaderProcess(context, filename, Path.Combine(root, "reader"), 0);
            using var writer = new SnapshotWriterProcess(filename, reader.Abort);
            writer.Execute("checkpoint"); // The child started and can complete real database work.
            using var writerProcess = Process.GetProcessById(writer.ProcessId);
            using var readerProcess = Process.GetProcessById(reader.ProcessId);
            Assert.True(db.BeginTrans()); // This live owner prevents the child's mutation from entering.
            try
            {
                var mutation = new BsonDocument
                {
                    ["changes"] = new BsonArray { new BsonDocument { ["_id"] = 1, ["delete"] = true } }
                };
                var clock = Stopwatch.StartNew();
                var failure = Assert.Throws<FuzzFailureException>(() =>
                    writer.Execute(Convert.ToBase64String(BsonSerializer.Serialize(mutation)), TimeSpan.FromSeconds(1)));
                Assert.Equal("SNAPSHOT_WRITER_TIMEOUT", failure.FailureId);
                Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10));
                Assert.True(writerProcess.HasExited);
                Assert.True(readerProcess.HasExited);
            }
            finally { db.Rollback(); }
            db.Dispose();
            using var reopened = new LiteDatabase(new ConnectionString { Filename = filename, Connection = ConnectionType.Shared });
            Assert.Equal(42, reopened.GetCollection("rows").FindById(1)["value"].AsInt32);
            Assert.Equal(9000, reopened.GetCollection("rows").FindById(1)["payload"].AsBinary.Length);
            reopened.Checkpoint();
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Bounded_writer_keeps_three_generations_and_matches_the_model()
    {
        var root = Path.Combine(Path.GetTempPath(), "litedb-snapshot-progress-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var context = new FuzzContext("snapshot", 3013, 2, null, root);
            await new SnapshotFuzzer().RunAsync(context);
            Assert.Equal(3, context.Metrics["simultaneousSnapshotGenerations"]);
            Assert.Equal(6, context.Metrics["snapshotValidations"]);
            Assert.Equal(14, context.Metrics["checkpointsBetweenReaderGenerations"]);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
