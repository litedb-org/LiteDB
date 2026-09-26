#if NET8_0_OR_GREATER
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class SharedCoordinatedResources_Tests
    {
        [Fact]
        public void Failed_open_balances_publication_and_the_next_open_marks_storage_change()
        {
            WithFile(file =>
            {
                using (var engine = new SharedEngine(new EngineSettings { Filename = file }))
                using (var database = new LiteDatabase(engine))
                {
                    var rows = database.GetCollection("rows");
                    rows.Insert(new BsonDocument { ["_id"] = 1, ["value"] = 19 });
                    rows.FindById(1);
                    var page = Field<SharedCoordinationPage>(engine, "_coordination");
                    page.Should().NotBeNull();
                    engine.SimulateOpenEngine = () => throw new IOException("injected open failure");
                    Action failed = () => rows.Update(new BsonDocument { ["_id"] = 1, ["value"] = 20 });
                    try { failed.Should().Throw<IOException>().WithMessage("injected open failure"); }
                    finally { engine.SimulateOpenEngine = null; }
                    page.TryRead(out var afterFailure).Should().BeTrue("failed opens must end structural publication");
                    var observed = 0;
                    engine.CoordinationStage = stage =>
                    {
                        if (stage != "opening") return;
                        observed++;
                        page.TryRead(out _).Should().BeFalse("the next open must publish its own structural begin");
                    };
                    rows.Update(new BsonDocument { ["_id"] = 1, ["value"] = 21 }).Should().BeTrue();
                    engine.CoordinationStage = null;
                    observed.Should().Be(1);
                    page.TryRead(out var afterSuccess).Should().BeTrue();
                    afterFailure.SameStorage(afterSuccess).Should().BeFalse();
                    rows.FindById(1)["value"].AsInt32.Should().Be(21);
                }
                using (var cold = new LiteDatabase(file))
                    cold.GetCollection("rows").FindById(1)["value"].AsInt32.Should().Be(21);
            });
        }

        [Fact]
        public void Completed_spilling_sort_releases_its_cached_engine_while_connection_remains_alive()
        {
            WithFile(file =>
            {
                using (var seed = new LiteDatabase(file))
                    seed.GetCollection("rows").InsertBulk(Enumerable.Range(0, 200).Select(id =>
                        new BsonDocument { ["_id"] = id, ["key"] = (199 - id).ToString("D3") + new string('s', 100) }));
                using (var engine = new SharedEngine(new EngineSettings { Filename = file })
                    { CoordinatedIdleLimit = TimeSpan.FromMinutes(1) })
                using (var database = new LiteDatabase(engine))
                {
                    var rows = database.GetCollection("rows");
                    for (var i = 0; i < 3; i++) rows.FindById(0);
                    engine.HasCachedSnapshot.Should().BeTrue();
                    var cached = Field<object>(engine, "_cachedSnapshot");
                    var snapshot = (LiteEngine)cached.GetType().GetProperty("Engine", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(cached);
                    var sortField = typeof(LiteEngine).GetField("_sortDisk", BindingFlags.Instance | BindingFlags.NonPublic);
                    ((SortDisk)sortField.GetValue(snapshot)).Dispose();
                    var settings = Field<EngineSettings>(snapshot, "_settings");
                    var header = Field<HeaderPage>(snapshot, "_header");
                    // A small real file-backed sorter isolates spill retention from the page-cache bound.
                    var sort = new SortDisk(settings.CreateTempFactory(), Constants.PAGE_SIZE, header.Pragmas);
                    sortField.SetValue(snapshot, sort);
                    var hits = engine.CoordinatedReadHits;
                    rows.Query().OrderBy("key").ToArray().Select(row => row["_id"].AsInt32)
                        .Should().Equal(Enumerable.Range(0, 200).Reverse());
                    sort.HasSpilled.Should().BeTrue();
                    engine.CoordinatedReadHits.Should().BeGreaterThan(hits);
                    engine.HasCachedSnapshot.Should().BeFalse("a completed spill must not remain in idle cached storage");
                    Directory.GetFiles(Path.GetDirectoryName(file), "*-tmp*").Should().BeEmpty();
                    rows.FindById(0)["key"].AsString.Should().StartWith("199");
                    GC.KeepAlive(database);
                }
            });
        }

        [Fact]
        public void First_read_skips_authority_attachment_but_a_later_write_still_joins_it()
        {
            WithFile(file =>
            {
                using (var peer = new LiteDatabase(new ConnectionString { Filename = file, Connection = ConnectionType.Shared }))
                using (var engine = new SharedEngine(new EngineSettings { Filename = file }))
                using (var database = new LiteDatabase(engine))
                {
                    var peerRows = peer.GetCollection("rows");
                    peerRows.Insert(new BsonDocument { ["_id"] = 1, ["value"] = 19 });
                    for (var i = 0; i < 3; i++) peerRows.FindById(1);
                    File.Exists(SharedCoordinationPage.PagePath(file)).Should().BeTrue();
                    var rows = database.GetCollection("rows");
                    rows.FindById(1)["value"].AsInt32.Should().Be(19);
                    Field<SharedCoordinationPage>(engine, "_coordination").Should().BeNull();
                    engine.HasCachedSnapshot.Should().BeFalse();
                    rows.Update(new BsonDocument { ["_id"] = 1, ["value"] = 20 }).Should().BeTrue();
                    Field<SharedCoordinationPage>(engine, "_coordination").Should().NotBeNull();
                    peerRows.FindById(1)["value"].AsInt32.Should().Be(20);
                }
                using (var cold = new LiteDatabase(file))
                    cold.GetCollection("rows").FindById(1)["value"].AsInt32.Should().Be(20);
            });
        }

        private static T Field<T>(object owner, string name) =>
            (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(owner);

        private static void WithFile(Action<string> test)
        {
            var directory = Path.Combine(Path.GetTempPath(), "litedb-coordinated-resources-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try { test(Path.Combine(directory, "test.db")); }
            finally { Directory.Delete(directory, true); }
        }
    }
}
#endif
