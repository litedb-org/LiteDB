using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2827_Tests
    {
        [Fact]
        public void Ensure_index_sees_pages_allocated_in_the_uncommitted_transaction()
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            db.BeginTrans();
            rows.Insert(Enumerable.Range(1, 6000).Select(i =>
                new BsonDocument { ["_id"] = i, ["value"] = i * 17 }));
            rows.EnsureIndex("value", true).Should().BeTrue();
            db.Commit();
            rows.FindAll().OrderBy(x => x["_id"].AsInt32).Select(x => x["value"].AsInt32)
                .Should().Equal(Enumerable.Range(1, 6000).Select(i => i * 17));
            rows.FindOne(Query.EQ("value", 6000 * 17))["_id"].AsInt32.Should().Be(6000);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Enlarged_traversal_bound_still_rejects_a_real_index_cycle(bool corruptHeader)
        {
            using var engine = new LiteEngine(new EngineSettings { Filename = ":memory:" });
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
            var monitor = engine.GetMonitor();
            var transaction = monitor.GetTransaction(true, false, out _);
            try
            {
                var snapshot = transaction.CreateSnapshot(LockMode.Write, "rows", false);
                var index = snapshot.CollectionPage.GetCollectionIndex("_id");
                var indexer = new IndexService(snapshot, Collation.Binary, 2550);
                var node = indexer.FindAll(index, Query.Ascending).Single();
                node.SetNext(0, node.Position);
                var header = snapshot.GetPage<HeaderPage>(0);
                var lastPageID = header.LastPageID;
                try
                {
                    if (corruptHeader) header.LastPageID = uint.MaxValue;
                    Action read = () => indexer.FindAll(index, Query.Ascending).Take(10000).ToArray();
                    read.Should().Throw<LiteException>().WithMessage("*Detected loop in FindAll*");
                }
                finally
                {
                    header.LastPageID = lastPageID;
                }
            }
            finally
            {
                transaction.Rollback();
                monitor.ReleaseTransaction(transaction);
            }
        }

        [Theory]
        [InlineData(2400, true)]
        [InlineData(3000, false)]
        [InlineData(3000, true)]
        [InlineData(10000, true)]
        public void Rebuild_preserves_every_document_and_secondary_index_across_loop_guard_boundary(int count, bool indexed)
        {
            var directory = Path.Combine(Path.GetTempPath(), "litedb-2827-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, "data.db");
            try
            {
                using (var db = new LiteDatabase(file))
                {
                    var col = db.GetCollection("rows");
                    col.InsertBulk(Enumerable.Range(1, count).Select(i =>
                        new BsonDocument { ["_id"] = i, ["name"] = "user" + i, ["value"] = i * 17 }));
                    if (indexed) col.EnsureIndex("name", true);
                    db.Rebuild();
                    col.Count().Should().Be(count);
                }
                using (var db = new LiteDatabase(file))
                {
                    var col = db.GetCollection("rows");
                    var rows = col.FindAll().OrderBy(x => x["_id"].AsInt32).ToArray();
                    rows.Select(x => x["_id"].AsInt32).Should().Equal(Enumerable.Range(1, count));
                    rows.Select(x => x["value"].AsInt32).Should().Equal(Enumerable.Range(1, count).Select(i => i * 17));
                    col.Find(Query.EQ("name", "user" + count)).Single()["_id"].AsInt32.Should().Be(count);
                    if (indexed)
                    {
                        col.EnsureIndex("name", true).Should().BeFalse("rebuild must retain the original index");
                        Action duplicate = () => col.Insert(new BsonDocument { ["_id"] = count + 1, ["name"] = "user1" });
                        duplicate.Should().Throw<LiteException>().Which.ErrorCode.Should().Be(LiteException.INDEX_DUPLICATE_KEY);
                    }
                }
            }
            finally { Directory.Delete(directory, true); }
        }
    }
}
