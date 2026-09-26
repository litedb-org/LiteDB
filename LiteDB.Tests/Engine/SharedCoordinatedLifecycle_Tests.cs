#if NET8_0_OR_GREATER
using System;
using System.IO;
using System.Linq;
using System.Threading;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class SharedCoordinatedLifecycle_Tests
    {
        [Theory]
        [InlineData("cached-status")]
        [InlineData("lease-published")]
        public void Rebuild_orders_its_lease_scan_with_mapped_admission(string interleaving)
        {
            WithFile(file =>
            {
                using (var writer = new LiteDatabase(new ConnectionString { Filename = file, Connection = ConnectionType.Shared }))
                using (var engine = new SharedEngine(new EngineSettings { Filename = file }))
                using (var reader = new LiteDatabase(engine))
                {
                    var rows = writer.GetCollection("rows");
                    rows.InsertBulk(Enumerable.Range(0, 40).Select(id => new BsonDocument
                        { ["_id"] = id, ["key"] = 40 - id, ["payload"] = new string('r', 4000) }));
                    rows.EnsureIndex("key");
                    writer.Checkpoint();
                    reader.GetCollection("rows").FindById(0)["key"].AsInt32.Should().Be(40);
                    engine.MutexOwner.WaitForRelease();
                    var transitions = 0;
                    engine.CoordinationStage = stage =>
                    {
                        if (stage != interleaving) return;
                        engine.CoordinationStage = null;
                        transitions++;
                        Action rebuild = () => writer.Rebuild();
                        if (stage == "lease-published")
                            rebuild.Should().Throw<LiteException>().WithMessage("*Close shared readers*");
                        else rebuild.Should().NotThrow();
                    };
                    var actual = reader.GetCollection("rows").FindById(39);
                    actual["key"].AsInt32.Should().Be(1);
                    actual["payload"].AsString.Should().Be(new string('r', 4000));
                    transitions.Should().Be(1);
                    reader.GetCollection("rows").Query().OrderBy("key").Select("_id").ToArray()
                        .Select(row => row["_id"].AsInt32).Should().Equal(Enumerable.Range(0, 40).Reverse());
                }
            });
        }

        [Fact]
        public void Idle_snapshot_expires_without_checkpointing_or_retaining_a_lease()
        {
            WithFile(file =>
            {
                using (var writer = new LiteDatabase(new ConnectionString { Filename = file, Connection = ConnectionType.Shared }))
                using (var engine = new SharedEngine(new EngineSettings { Filename = file }))
                using (var reader = new LiteDatabase(engine))
                using (var registry = new SharedReaderRegistry(file))
                {
                    writer.CheckpointSize = 0;
                    writer.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = 19 });
                    reader.GetCollection("rows").FindById(1)["value"].AsInt32.Should().Be(19);
                    reader.GetCollection("rows").FindById(1)["value"].AsInt32.Should().Be(19);
                    engine.CoordinatedReadHits.Should().BeGreaterThan(0);
                    registry.LiveVersions().Should().BeEmpty();
                    var log = Path.Combine(Path.GetDirectoryName(file), "test-log.db");
                    var bytes = File.ReadAllBytes(log);
                    Thread.Sleep(500);
                    File.ReadAllBytes(log).Should().Equal(bytes);
                    var opens = engine.SnapshotOpens;
                    reader.GetCollection("rows").FindById(1)["value"].AsInt32.Should().Be(19);
                    engine.SnapshotOpens.Should().Be(opens + 1);
                    GC.KeepAlive(reader);
                }
            });
        }

        [Fact]
        public void Unknown_mapping_falls_back_without_altering_foreign_bytes()
        {
            WithFile(file =>
            {
                var foreign = new byte[] { 17, 33, 51 };
                var page = SharedCoordinationPage.PagePath(file);
                File.WriteAllBytes(page, foreign);
                using (var engine = new SharedEngine(new EngineSettings { Filename = file }))
                using (var database = new LiteDatabase(engine))
                {
                    database.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = 71 });
                    for (var i = 0; i < 10; i++)
                        database.GetCollection("rows").FindById(1)["value"].AsInt32.Should().Be(71);
                    engine.CoordinatedReadHits.Should().Be(0);
                    engine.CoordinationFallbackReason.Should().Contain("Unknown Shared status page size");
                }
                File.ReadAllBytes(page).Should().Equal(foreign);
                using (var cold = new LiteDatabase(file))
                    cold.GetCollection("rows").FindById(1)["value"].AsInt32.Should().Be(71);
            });
        }

        private static void WithFile(Action<string> test)
        {
            var directory = Path.Combine(Path.GetTempPath(), "litedb-coordinated-lifecycle-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try { test(Path.Combine(directory, "test.db")); }
            finally { Directory.Delete(directory, true); }
        }
    }
}
#endif
