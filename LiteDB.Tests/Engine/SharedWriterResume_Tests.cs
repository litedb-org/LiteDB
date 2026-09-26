#if NET8_0_OR_GREATER
using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class SharedWriterResume_Tests
    {
        [Fact]
        public void Large_WAL_does_not_retain_writer_metadata_or_require_a_checkpoint()
        {
            WithFile(file =>
            {
                using (var engine = new SharedEngine(new EngineSettings { Filename = file }))
                using (var writer = new LiteDatabase(engine))
                {
                    writer.CheckpointSize = 0;
                    var rows = writer.GetCollection("rows");
                    rows.InsertBulk(Enumerable.Range(0, 5000).Select(id => Row(id, 0)));
                    rows.Update(Row(0, 1));
                    var log = Path.Combine(Path.GetDirectoryName(file), "test-log.db");
                    new FileInfo(log).Length.Should().BeGreaterThan(16L * 1024 * 1024);
                    var retained = typeof(SharedEngine).GetField("_writerResume",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    retained.GetValue(engine).Should().BeNull();
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    retained.GetValue(engine).Should().BeNull();
                    GC.KeepAlive(writer);
                }
                using (var cold = new LiteDatabase(file))
                {
                    cold.GetCollection("rows").Count().Should().Be(5000);
                    cold.GetCollection("rows").FindById(0)["payload"].AsString.Should().Be(Payload(1));
                }
            });
        }

        [Fact]
        public void Reused_prefix_with_a_growing_WAL_requires_full_replay_and_preserves_peer_commit()
        {
            WithFile(file =>
            {
                using (var engine = new SharedEngine(new EngineSettings { Filename = file }))
                using (var writer = new LiteDatabase(engine))
                using (var peer = new LiteDatabase(new ConnectionString { Filename = file, Connection = ConnectionType.Shared }))
                using (var old = new LiteDatabase(new ConnectionString { Filename = file, Connection = ConnectionType.Shared }))
                {
                    writer.CheckpointSize = 0;
                    var rows = writer.GetCollection("rows");
                    rows.InsertBulk(Enumerable.Range(0, 200).Select(id => Row(id, 0)));
                    rows.EnsureIndex("value");
                    using (var held = old.GetCollection("rows").Query().OrderBy("_id").ToEnumerable().GetEnumerator())
                    {
                        held.MoveNext().Should().BeTrue();
                        rows.Update(Enumerable.Range(0, 200).Select(id => Row(id, 1)));
                        rows.Update(Enumerable.Range(0, 200).Select(id => Row(id, 2)));
                        writer.Checkpoint();
                        rows.Update(Row(199, 3));
                        var cached = (SharedWriterResume)typeof(SharedEngine).GetField("_writerResume",
                            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(engine);
                        cached.Should().NotBeNull("the invalidation test must start with a reusable writer prefix");
                        cached.FreeLogPositions.Count.Should().BeGreaterThan(0);
                        var page = (SharedCoordinationPage)typeof(SharedEngine).GetField("_coordination",
                            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(engine);
                        page.TryRead(out var before).Should().BeTrue();
                        var resumed = engine.WriterResumeCount;
                        var log = Path.Combine(Path.GetDirectoryName(file), "test-log.db");
                        var length = new FileInfo(log).Length;
                        peer.GetCollection("rows").Update(Row(198, 4));
                        page.TryRead(out var after).Should().BeTrue();
                        after.Reuse.Should().BeGreaterThan(before.Reuse, "this must overwrite reclaimed physical slots");
                        new FileInfo(log).Length.Should().BeGreaterOrEqualTo(length);
                        rows.Update(Row(197, 5));
                        engine.WriterResumeCount.Should().Be(resumed, "length and the old confirmation cannot prove an unchanged prefix");
                        rows.FindById(198)["payload"].AsString.Should().Be(Payload(4));
                        rows.Query().Where("value = 400").ToArray().Select(row => row["_id"].AsInt32).Should().Equal(198);
                        var count = 1;
                        while (held.MoveNext())
                        {
                            held.Current["_id"].AsInt32.Should().Be(count++);
                            held.Current["payload"].AsString.Should().Be(Payload(0));
                        }
                        count.Should().Be(200);
                    }
                    writer.Checkpoint();
                }
                using (var cold = new LiteDatabase(file))
                {
                    cold.GetCollection("rows").Count().Should().Be(200);
                    cold.GetCollection("rows").FindById(198)["payload"].AsString.Should().Be(Payload(4));
                }
            });
        }

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void Append_only_peer_commits_resume_detached_metadata_and_keep_indexes_exact(string password)
        {
            WithFile(file =>
            {
                var settings = new EngineSettings { Filename = file, Password = password };
                using (var engine = new SharedEngine(settings))
                using (var first = new LiteDatabase(engine))
                using (var second = new LiteDatabase(new ConnectionString
                    { Filename = file, Password = password, Connection = ConnectionType.Shared }))
                {
                    first.CheckpointSize = 0;
                    var left = first.GetCollection("rows");
                    var right = second.GetCollection("rows");
                    left.InsertBulk(Enumerable.Range(0, 40).Select(id => Row(id, 0)));
                    left.EnsureIndex("value");
                    // Alternate owners while repeatedly updating the same records. The
                    // physical prefix is append-only, but the logical index changes.
                    var before = engine.WriterResumeCount;
                    for (var revision = 1; revision <= 8; revision++)
                    {
                        right.Update(Row(revision, revision));
                        left.Update(Row(0, revision));
                        using (var fresh = new LiteDatabase(new ConnectionString
                            { Filename = file, Password = password, Connection = ConnectionType.Shared }))
                        {
                            var rows = fresh.GetCollection("rows");
                            rows.FindById(0)["value"].AsInt32.Should().Be(revision * 100);
                            rows.FindById(revision)["payload"].AsString.Should().Be(Payload(revision));
                            rows.Query().Where("value = @0", revision * 100).ToArray().Select(row => row["_id"].AsInt32)
                                .Should().BeEquivalentTo(new[] { 0, revision });
                            rows.Count().Should().Be(40);
                        }
                    }
                    engine.WriterResumeCount.Should().BeGreaterThan(before);
                }
                using (var cold = new LiteDatabase(new ConnectionString { Filename = file, Password = password }))
                {
                    var rows = cold.GetCollection("rows").FindAll().OrderBy(row => row["_id"].AsInt32).ToArray();
                    rows.Length.Should().Be(40);
                    for (var id = 0; id < 40; id++)
                    {
                        var revision = id == 0 ? 8 : id <= 8 ? id : 0;
                        rows[id]["value"].AsInt32.Should().Be(revision * 100);
                        rows[id]["payload"].AsString.Should().Be(Payload(revision));
                    }
                }
            });
        }

        [Theory]
        [InlineData("checkpoint")]
        [InlineData("rebuild")]
        public void Changed_storage_generation_rejects_cached_writer_state(string transition)
        {
            WithFile(file =>
            {
                using (var engine = new SharedEngine(new EngineSettings { Filename = file }))
                using (var writer = new LiteDatabase(engine))
                using (var peer = new LiteDatabase(new ConnectionString { Filename = file, Connection = ConnectionType.Shared }))
                {
                    writer.CheckpointSize = 0;
                    var rows = writer.GetCollection("rows");
                    rows.Insert(Row(0, 0));
                    rows.Update(Row(0, 1));
                    rows.Update(Row(0, 2));
                    engine.WriterResumeCount.Should().BeGreaterThan(0);
                    var before = engine.WriterResumeCount;
                    if (transition == "checkpoint") peer.Checkpoint();
                    else peer.Rebuild();
                    rows.Update(Row(0, 3));
                    engine.WriterResumeCount.Should().Be(before, "changed storage requires a full replay");
                    peer.GetCollection("rows").FindById(0)["payload"].AsString.Should().Be(Payload(3));
                }
                using (var cold = new LiteDatabase(file))
                    cold.GetCollection("rows").FindById(0)["value"].AsInt32.Should().Be(300);
            });
        }

        [Fact]
        public void Rollback_does_not_resurrect_unconfirmed_documents_after_resume()
        {
            WithFile(file =>
            {
                using (var engine = new SharedEngine(new EngineSettings { Filename = file, TransactionPageLimit = 1 }))
                using (var writer = new LiteDatabase(engine))
                {
                    writer.CheckpointSize = 0;
                    var rows = writer.GetCollection("rows");
                    rows.Insert(Row(0, 0));
                    rows.Update(Row(0, 1));
                    writer.BeginTrans().Should().BeTrue();
                    rows.InsertBulk(Enumerable.Range(1, 40).Select(id => Row(id, 9)));
                    writer.Rollback().Should().BeTrue();
                    rows.Update(Row(0, 2));
                    rows.Update(Row(0, 3));
                    rows.Count().Should().Be(1);
                    rows.FindById(0)["payload"].AsString.Should().Be(Payload(3));
                }
                using (var cold = new LiteDatabase(file))
                {
                    cold.GetCollection("rows").Count().Should().Be(1);
                    cold.GetCollection("rows").FindById(0)["value"].AsInt32.Should().Be(300);
                }
            });
        }

        private static string Payload(int revision) => new string((char)('a' + revision), 4000);
        private static BsonDocument Row(int id, int revision) => new BsonDocument
            { ["_id"] = id, ["value"] = revision * 100, ["payload"] = Payload(revision) };

        private static void WithFile(Action<string> test)
        {
            var directory = Path.Combine(Path.GetTempPath(), "litedb-writer-resume-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try { test(Path.Combine(directory, "test.db")); }
            finally { Directory.Delete(directory, true); }
        }
    }
}
#endif
