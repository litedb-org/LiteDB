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
        [InlineData(false)]
        [InlineData(true)]
        public void Unavailable_new_authority_or_long_control_names_keep_existing_database_operations(bool longName)
        {
            WithFile(original =>
            {
                var file = longName ? Path.Combine(Path.GetDirectoryName(original), new string('d', 240) + ".db") : original;
                if (!longName)
                {
                    // Neither control-file creation nor publishing a new marker can
                    // succeed. There was no page and therefore no mapped authority.
                    Directory.CreateDirectory(SharedCoordinationFallback.LivePath(file));
                    Directory.CreateDirectory(SharedCoordinationPage.DisabledPath(file));
                }
                using (var engine = new SharedEngine(new EngineSettings
                    { Filename = file, SharedMutexNameStrategy = SharedMutexNameStrategy.Sha1Hash }))
                using (var database = new LiteDatabase(engine))
                {
                    database.CheckpointSize = 0;
                    database.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = 73 });
                    for (var i = 0; i < 3; i++)
                        database.GetCollection("rows").FindById(1)["value"].AsInt32.Should().Be(73);
                    engine.CoordinatedReadHits.Should().Be(0);
                }
                using (var cold = new LiteDatabase(file))
                    cold.GetCollection("rows").FindById(1)["value"].AsInt32.Should().Be(73);
                if (!longName)
                {
                    Directory.Exists(SharedCoordinationFallback.LivePath(file)).Should().BeTrue();
                    Directory.Exists(SharedCoordinationPage.DisabledPath(file)).Should().BeTrue();
                }
            });
        }

        [Fact]
        public void One_shot_read_does_not_create_coordination_files()
        {
            WithFile(file =>
            {
                using (var seed = new LiteDatabase(file))
                    seed.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
                using (var database = new LiteDatabase(new ConnectionString { Filename = file, Connection = ConnectionType.Shared }))
                {
                    database.GetCollection("rows").FindById(1)["_id"].AsInt32.Should().Be(1);
                    File.Exists(SharedCoordinationPage.PagePath(file)).Should().BeFalse();
                }
                File.Exists(SharedCoordinationPage.PagePath(file)).Should().BeFalse();
            });
        }

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
                    reader.GetCollection("rows").Query().OrderBy("key").ToArray()
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
                    var bytes = ReadShared(log);
                    SpinWait.SpinUntil(() => !engine.HasCachedSnapshot, TimeSpan.FromSeconds(5)).Should().BeTrue();
                    ReadShared(log).Should().Equal(bytes);
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

        private static byte[] ReadShared(string path)
        {
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var copy = new MemoryStream())
            {
                file.CopyTo(copy);
                return copy.ToArray();
            }
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
