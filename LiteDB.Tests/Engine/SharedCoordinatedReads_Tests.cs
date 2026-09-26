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
    public class SharedCoordinatedReads_Tests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Checkpoint_between_status_read_and_lease_publication_requires_revalidation(bool unsafeSkip)
        {
            WithFile(file =>
            {
                using (var writer = new LiteDatabase(new ConnectionString { Filename = file, Connection = ConnectionType.Shared }))
                using (var engine = new SharedEngine(new EngineSettings { Filename = file }) { CoordinatedIdleLimit = TimeSpan.FromMinutes(1) })
                using (var reader = new LiteDatabase(engine))
                {
                    writer.CheckpointSize = 0;
                    writer.GetCollection("rows").InsertBulk(Enumerable.Range(0, 40).Select(id =>
                        new BsonDocument { ["_id"] = id, ["value"] = id * 13, ["payload"] = new string('p', 4000) }));
                    reader.GetCollection("rows").FindById(0)["value"].AsInt32.Should().Be(0);
                    reader.GetCollection("rows").FindById(0)["value"].AsInt32.Should().Be(0);
                    engine.MutexOwner.WaitForRelease();
                    var transitions = 0;
                    engine.UnsafeSkipCoordinationRecheck = unsafeSkip;
                    engine.CoordinationStage = stage =>
                    {
                        if (stage != "cached-status") return;
                        engine.CoordinationStage = null;
                        writer.Checkpoint();
                        transitions++;
                    };
                    Action read = () =>
                    {
                        var row = reader.GetCollection("rows").FindById(39);
                        row["value"].AsInt32.Should().Be(39 * 13);
                        row["payload"].AsString.Should().Be(new string('p', 4000));
                    };
                    if (unsafeSkip) read.Should().Throw<Exception>("the mutant trusts WAL addresses reclaimed before lease publication");
                    else read.Should().NotThrow();
                    transitions.Should().Be(1, "the checkpoint must occur inside fast admission");
                }
            });
        }

        [Fact]
        public void Repeated_reads_use_protected_cache_and_a_peer_commit_invalidates_it()
        {
            WithFile(file =>
            {
                using (var writer = new LiteDatabase(new ConnectionString { Filename = file, Connection = ConnectionType.Shared }))
                using (var engine = new SharedEngine(new EngineSettings { Filename = file }) { CoordinatedIdleLimit = TimeSpan.FromMinutes(1) })
                using (var reader = new LiteDatabase(engine))
                {
                    var rows = writer.GetCollection("rows");
                    rows.Insert(new BsonDocument { ["_id"] = 1, ["value"] = "first" });
                    reader.GetCollection("rows").FindById(1)["value"].AsString.Should().Be("first");
                    reader.GetCollection("rows").FindById(1)["value"].AsString.Should().Be("first");
                    for (var attempt = 0; attempt < 10; attempt++)
                        reader.GetCollection("rows").FindById(1)["value"].AsString.Should().Be("first");
                    var pageField = typeof(SharedEngine).GetField("_coordination", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    var page = (SharedCoordinationPage)pageField.GetValue(engine);
                    engine.CoordinatedReadHits.Should().BeGreaterThan(0,
                        "page exists={0}, snapshots={1}, owned={2}, page file={3}, reason={4}", page != null, engine.SnapshotOpens,
                        engine.MutexOwner.IsOwnedByCurrentThread, File.Exists(SharedCoordinationPage.PagePath(file)), engine.CoordinationFallbackReason);
                    rows.Update(new BsonDocument { ["_id"] = 1, ["value"] = "second" });
                    reader.GetCollection("rows").FindById(1)["value"].AsString.Should().Be("second");
                    reader.GetCollection("rows").FindById(1)["value"].AsString.Should().Be("second");
                }
                File.Exists(SharedCoordinationPage.PagePath(file)).Should().BeFalse();
                File.Exists(Path.Combine(Path.GetDirectoryName(file), "test-log.db")).Should().BeFalse();
            });
        }

        [Fact]
        public void Revocation_preserves_open_snapshot_and_routes_new_queries_through_mutex()
        {
            WithFile(file =>
            {
                using (var writer = new LiteDatabase(new ConnectionString { Filename = file, Connection = ConnectionType.Shared }))
                using (var engine = new SharedEngine(new EngineSettings { Filename = file }) { CoordinatedIdleLimit = TimeSpan.FromMinutes(1) })
                using (var reader = new LiteDatabase(engine))
                {
                    var rows = writer.GetCollection("rows");
                    rows.InsertBulk(Enumerable.Range(0, 200).Select(id => new BsonDocument { ["_id"] = id, ["value"] = "original" }));
                    using (var held = reader.GetCollection("rows").Query().OrderBy("_id").ToEnumerable().GetEnumerator())
                    {
                        held.MoveNext().Should().BeTrue();
                        SharedCoordinationPage.Revoke(file);
                        rows.Update(new BsonDocument { ["_id"] = 1, ["value"] = "updated" });
                        writer.Checkpoint();
                        var count = 1;
                        while (held.MoveNext())
                        {
                            held.Current["_id"].AsInt32.Should().Be(count++);
                            held.Current["value"].AsString.Should().Be("original");
                        }
                        count.Should().Be(200);
                    }
                    var hits = engine.CoordinatedReadHits;
                    reader.GetCollection("rows").FindById(1)["value"].AsString.Should().Be("updated");
                    engine.CoordinatedReadHits.Should().Be(hits);
                }
            });
        }

        private static void WithFile(Action<string> test)
        {
            var directory = Path.Combine(Path.GetTempPath(), "litedb-mapped-reads-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try { test(Path.Combine(directory, "test.db")); }
            finally { Directory.Delete(directory, true); }
        }
    }
}
#endif
