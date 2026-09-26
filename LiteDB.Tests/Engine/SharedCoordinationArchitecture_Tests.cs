#if NET8_0_OR_GREATER
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class SharedCoordinationArchitecture_Tests
    {
        [Fact]
        public void Protected_fallback_reads_do_not_revoke_peers_but_a_later_write_does()
        {
            using (var file = new TempFile())
            using (var peerEngine = new SharedEngine(new EngineSettings { Filename = file })
                { CoordinatedIdleLimit = TimeSpan.FromMinutes(1) })
            using (var peer = new LiteDatabase(peerEngine))
            using (var fallback = new SharedEngine(new EngineSettings { Filename = file })
                { CoordinationArchitectureOverride = Architecture.Arm })
            using (var database = new LiteDatabase(fallback))
            {
                peer.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = 19 });
                for (var i = 0; i < 3; i++) peer.GetCollection("rows").FindById(1);
                var hits = peerEngine.CoordinatedReadHits;
                hits.Should().BeGreaterThan(0);
                for (var i = 0; i < 2; i++) database.GetCollection("rows").FindById(1)["value"].AsInt32.Should().Be(19);
                fallback.CoordinationFallbackReason.Should().Be("architecture: Arm");
                peer.GetCollection("rows").FindById(1)["value"].AsInt32.Should().Be(19);
                peerEngine.CoordinatedReadHits.Should().BeGreaterThan(hits, "a protected read cannot invalidate storage");
                hits = peerEngine.CoordinatedReadHits;
                database.GetCollection("rows").Update(new BsonDocument { ["_id"] = 1, ["value"] = 20 });
                peer.GetCollection("rows").FindById(1)["value"].AsInt32.Should().Be(20);
                peerEngine.CoordinatedReadHits.Should().Be(hits, "a fallback writer must revoke before mutation");
            }
        }

        [Fact]
        public void Unqualified_architecture_revokes_admission_and_preserves_an_active_cached_reader()
        {
            var directory = Path.Combine(Path.GetTempPath(), "litedb-architecture-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, "test.db");
            try
            {
                using (var seed = new LiteDatabase(file))
                {
                    seed.GetCollection("rows").InsertBulk(Enumerable.Range(0, 200).Select(id =>
                        new BsonDocument { ["_id"] = id, ["value"] = "original", ["payload"] = new string('p', 1000) }));
                    seed.GetCollection("rows").EnsureIndex("value");
                }
                using (var readerEngine = new SharedEngine(new EngineSettings { Filename = file })
                    { CoordinatedIdleLimit = TimeSpan.FromMinutes(1) })
                using (var reader = new LiteDatabase(readerEngine))
                using (var fallback = new SharedEngine(new EngineSettings { Filename = file })
                    { CoordinationArchitectureOverride = Architecture.Arm })
                using (var writer = new LiteDatabase(fallback))
                {
                    var rows = reader.GetCollection("rows");
                    for (var i = 0; i < 3; i++) rows.FindById(0);
                    readerEngine.CoordinatedReadHits.Should().BeGreaterThan(0);
                    using (var held = rows.Query().OrderBy("_id").ToEnumerable().GetEnumerator())
                    {
                        held.MoveNext().Should().BeTrue();
                        writer.GetCollection("rows").Update(new BsonDocument
                            { ["_id"] = 1, ["value"] = "updated", ["payload"] = new string('p', 1000) });
                        fallback.CoordinationFallbackReason.Should().Be("architecture: Arm");
                        var before = readerEngine.CoordinatedReadHits;
                        var current = rows.FindById(1)["value"].AsString;
                        if (current != "updated") PreserveBeforeCleanup(directory);
                        current.Should().Be("updated", "a query after acknowledgement must not trust unannounced cached state");
                        readerEngine.CoordinatedReadHits.Should().Be(before);
                        writer.Checkpoint();
                        var count = 1;
                        while (held.MoveNext())
                        {
                            held.Current["_id"].AsInt32.Should().Be(count++);
                            held.Current["value"].AsString.Should().Be("original");
                            held.Current["payload"].AsString.Should().Be(new string('p', 1000));
                        }
                        count.Should().Be(200);
                    }
                    var hits = readerEngine.CoordinatedReadHits;
                    rows.FindById(1)["value"].AsString.Should().Be("updated");
                    readerEngine.CoordinatedReadHits.Should().Be(hits);
                    fallback.CoordinatedReadHits.Should().Be(0);
                }
                using (var cold = new LiteDatabase(file))
                    cold.GetCollection("rows").Find(Query.EQ("value", "updated")).Single()["_id"].AsInt32.Should().Be(1);
            }
            finally { Directory.Delete(directory, true); }
        }

        private static void PreserveBeforeCleanup(string directory)
        {
            var evidence = directory + "-before-cleanup";
            Directory.CreateDirectory(evidence);
            foreach (var file in Directory.GetFiles(directory))
            {
                using (var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var copy = File.Create(Path.Combine(evidence, Path.GetFileName(file))))
                    source.CopyTo(copy);
            }
            Console.WriteLine("Preserved before cleanup: " + evidence);
        }
    }
}
#endif
