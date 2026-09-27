using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Internals
{
    public class DocumentLookupCursor_Tests
    {
        [Theory]
        [InlineData(CompactStorageMode.Legacy, null)]
        [InlineData(CompactStorageMode.Auto, null)]
        [InlineData(CompactStorageMode.Legacy, "secret")]
        [InlineData(CompactStorageMode.Auto, "secret")]
        public void One_lookup_can_alternate_segmented_documents_and_projected_fields(CompactStorageMode mode, string password)
        {
            using var file = new TempFile();
            var expected = Enumerable.Range(1, 8).Select(Document).ToArray();
            using (var seed = new LiteDatabase(new ConnectionString { Filename = file.Filename, CompactStorage = mode, Password = password }))
            {
                seed.GetCollection("rows").InsertBulk(expected);
                if (mode == CompactStorageMode.Auto)
                    seed.Execute("SELECT $ FROM $dump WHERE $.pageType = 'Schema'").ToEnumerable().Should().NotBeEmpty();
            }
            var original = File.ReadAllBytes(file.Filename);
            using (var engine = new LiteEngine(new EngineSettings { Filename = file.Filename, Password = password, ReadOnly = true }))
            {
                var monitor = engine.GetMonitor();
                var transaction = monitor.GetTransaction(true, true, out _);
                try
                {
                    var snapshot = transaction.CreateSnapshot(LockMode.Read, "rows", false);
                    var indexer = new IndexService(snapshot, Collation.Binary, uint.MaxValue);
                    var data = new DataService(snapshot, uint.MaxValue);
                    var full = new DatafileLookup(data, true, null);
                    var selected = new DatafileLookup(data, true, new HashSet<string> { "_ID", "PAYLOAD" });
                    foreach (var id in new[] { 2, 1, 8, 3, 6, 7, 4, 5, 2, 8, 1 })
                    {
                        var node = indexer.Find(snapshot.CollectionPage.PK, id, false, Query.Ascending);
                        var actual = full.Load(node.DataBlock);
                        actual.RawId.Should().Be(node.DataBlock);
                        BsonSerializer.Serialize(actual).Should().Equal(BsonSerializer.Serialize(expected[id - 1]));
                        var projection = selected.Load(node);
                        projection.Keys.Should().BeEquivalentTo("_id", "payload");
                        projection["_id"].AsInt32.Should().Be(id);
                        projection["payload"].Should().Be(expected[id - 1]["payload"]);
                    }
                    var failingAddress = indexer.Find(snapshot.CollectionPage.PK, 2, false, Query.Ascending).DataBlock;
                    snapshot.Clear();
                    var reads = 0;
                    engine.SimulateDiskReadFail = _ =>
                    {
                        if (++reads == 2) throw new IOException("continuation read failed");
                    };
                    Action failed = () => full.Load(failingAddress);
                    failed.Should().Throw<IOException>();
                    reads.Should().Be(2);
                    engine.SimulateDiskReadFail = null;
                    foreach (var id in new[] { 1, 8, 2 })
                    {
                        var node = indexer.Find(snapshot.CollectionPage.PK, id, false, Query.Ascending);
                        BsonSerializer.Serialize(full.Load(node)).Should().Equal(BsonSerializer.Serialize(expected[id - 1]));
                    }
                }
                finally { monitor.ReleaseTransaction(transaction); }
                engine.GetMonitor().Transactions.Should().BeEmpty();
            }
            File.ReadAllBytes(file.Filename).Should().Equal(original);
        }

        [Fact]
        public void Failed_load_releases_its_borrowed_buffer_while_the_lookup_remains_alive()
        {
            using var file = new TempFile();
            using (var seed = new LiteDatabase(file.Filename))
            {
                seed.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["payload"] = new string('x', 300000) });
                seed.GetCollection("other").Insert(new BsonDocument { ["_id"] = 9, ["value"] = 123 });
            }
            var original = File.ReadAllBytes(file.Filename);
            var retained = LoadWithFailure(file.Filename);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            retained.Item2.IsAlive.Should().BeFalse("the failed load must not retain its last data-page segment");
            GC.KeepAlive(retained.Item1);
            File.ReadAllBytes(file.Filename).Should().Equal(original);
            using var reopened = new LiteDatabase(new ConnectionString { Filename = file.Filename, ReadOnly = true });
            reopened.GetCollection("rows").FindById(1)["payload"].AsString.Should().Be(new string('x', 300000));
            reopened.GetCollection("other").FindById(9)["value"].AsInt32.Should().Be(123);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Tuple<DatafileLookup, WeakReference> LoadWithFailure(string filename)
        {
            using var engine = new LiteEngine(new EngineSettings { Filename = filename, ReadOnly = true });
            var monitor = engine.GetMonitor();
            var transaction = monitor.GetTransaction(true, true, out _);
            var snapshot = transaction.CreateSnapshot(LockMode.Read, "rows", false);
            var indexer = new IndexService(snapshot, Collation.Binary, uint.MaxValue);
            var address = indexer.Find(snapshot.CollectionPage.PK, 1, false, Query.Ascending).DataBlock;
            var lookup = new DatafileLookup(new DataService(snapshot, uint.MaxValue), true, null);
            var reads = 0;
            WeakReference buffer = null;
            engine.SimulateDiskReadFail = page =>
            {
                reads++;
                if (reads == 19) buffer = new WeakReference(page.Array);
                if (reads == 20) throw new IOException("document read failed");
            };
            Action load = () => lookup.Load(address);
            load.Should().Throw<IOException>().WithMessage("document read failed");
            reads.Should().Be(20);
            buffer.Should().NotBeNull();
            engine.SimulateDiskReadFail = null;
            // Release the snapshot's own page references, as at a read safepoint.
            // The still-live lookup must not independently retain the failed cursor's page.
            snapshot.Clear();
            monitor.ReleaseTransaction(transaction);
            return Tuple.Create(lookup, buffer);
        }

        private static BsonDocument Document(int id)
        {
            var result = new BsonDocument { ["_id"] = id, ["payload"] = id % 2 == 0 ? new string((char)('a' + id), 25000) : "small" };
            if (id % 2 == 0)
                for (var i = 0; i < 40; i++) result["Property" + i + new string('z', 70)] = id + i;
            return result;
        }
    }
}
