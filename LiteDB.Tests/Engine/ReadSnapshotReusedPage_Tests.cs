using System;
using System.Linq;
using System.Threading;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Engine
{
    /// <summary>
    /// A read snapshot keeps the version it pinned. When another transaction frees one of
    /// its pages and this transaction reuses that page ID for a different collection, a
    /// safepoint puts the page in the transaction's dirty-page map. The read snapshot must
    /// still resolve that ID to the version it pinned, not to the transaction's own write.
    /// </summary>
    public class ReadSnapshotReusedPage_Tests
    {
        private const int Rows = 600;
        private const int Keys = 10;

        private static ConnectionString Settings(string file, bool encrypted) =>
            new ConnectionString { Filename = file, Connection = ConnectionType.Direct,
                Password = encrypted ? "secret" : null, TransactionPageLimit = 1 };

        private static void Seed(LiteDatabase db)
        {
            var source = db.GetCollection("source");
            source.EnsureIndex("s", "$.s");
            source.Insert(Enumerable.Range(1, Rows).Select(i => new BsonDocument
            {
                ["_id"] = i, ["s"] = "key-" + (i % Keys) + new string('x', 40), ["v"] = i % 7
            }));
            db.GetCollection("target").Insert(new BsonDocument { ["_id"] = 0 });
            db.Checkpoint();
        }

        private static BsonDocument[] Filler() => Enumerable.Range(1, 40)
            .Select(i => new BsonDocument { ["_id"] = i, ["payload"] = new string((char)('a' + i % 26), 1500) })
            .ToArray();

        private static string Key(int key) => "key-" + key + new string('x', 40);

        private static int[] Pages(LiteDatabase db, string collection, string type) =>
            db.Execute("SELECT $ FROM $dump").ToEnumerable()
                .Where(x => x["collection"].AsString == collection && x["pageType"].AsString == type)
                .Select(x => x["pageID"].AsInt32).ToArray();

        private static int[] FreeIndexPages(LiteDatabase db)
        {
            var before = Pages(db, "source", "Index");
            db.GetCollection("source").DropIndex("s").Should().BeTrue();
            return before.Except(Pages(db, "source", "Index")).ToArray();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Handle_read_snapshot_ignores_its_own_writes_to_a_reused_page(bool encrypted)
        {
            using var file = new TempFile();
            using var db = new LiteDatabase(Settings(file, encrypted));
            Seed(db);
            var expected = Rows / Keys;

            using var tx = db.BeginTransaction();
            var source = tx.GetCollection("source");
            // Pins the read snapshot of "source" with its "s" index (positive control).
            source.Count(Query.EQ("s", Key(3))).Should().Be(expected);

            // Another transaction frees the index pages; they go to the free list on commit.
            var freed = FreeIndexPages(db);
            freed.Should().NotBeEmpty();

            // The handle reuses freed page IDs for "target" and safepoints them (limit 1).
            tx.GetCollection("target").Insert(Filler()).Should().Be(40);

            // Read-your-writes still resolves the handle's own safepointed pages.
            tx.GetCollection("target").Count().Should().Be(41);

            // The pinned snapshot still seeks the dropped index and must read its own version.
            source.Query().Where("s = @0", Key(5)).GetPlan()["index"]["mode"].AsString.Should().StartWith("INDEX SEEK(s");
            for (var key = 0; key < Keys; key++)
                source.Count(Query.EQ("s", Key(key))).Should().Be(expected);
            source.Query().Where("s = @0", Key(5)).Select("$._id").ToArray()
                .Select(x => x["_id"].AsInt32).Should().BeEquivalentTo(Enumerable.Range(1, Rows).Where(i => i % Keys == 5));

            tx.Commit();
            // The handle did reuse freed index pages for the other collection.
            Pages(db, "target", "Data").Concat(Pages(db, "target", "Index")).Intersect(freed).Should().NotBeEmpty();
            db.GetCollection("target").Count().Should().Be(41);
            db.GetCollection("source").Count().Should().Be(Rows);
        }

        [Fact]
        public void Page_dump_inside_a_transaction_still_lists_its_own_safepointed_pages()
        {
            // The "$" page view is not a collection snapshot: it keeps showing the
            // transaction's own pages, including ones beyond the committed data file.
            using var file = new TempFile();
            using var db = new LiteDatabase(Settings(file, false));
            Seed(db);
            var committed = Pages(db, "target", "Data").Length;
            db.BeginTrans().Should().BeTrue();
            try
            {
                db.GetCollection("target").Insert(Filler()).Should().Be(40);
                Pages(db, "target", "Data").Length.Should().BeGreaterThan(committed);
            }
            finally { db.Rollback(); }
            Pages(db, "target", "Data").Length.Should().Be(committed);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Legacy_read_snapshot_ignores_its_own_writes_to_a_reused_page(bool encrypted)
        {
            using var file = new TempFile();
            using var db = new LiteDatabase(Settings(file, encrypted));
            Seed(db);
            var expected = Rows / Keys;

            Exception failure = null;
            using var pinned = new ManualResetEventSlim();
            using var dropped = new ManualResetEventSlim();
            var owner = new Thread(() =>
            {
                try
                {
                    db.BeginTrans().Should().BeTrue();
                    var source = db.GetCollection("source");
                    source.Count(Query.EQ("s", Key(3))).Should().Be(expected);
                    pinned.Set();
                    dropped.Wait(TimeSpan.FromSeconds(30)).Should().BeTrue();

                    db.GetCollection("target").Insert(Filler()).Should().Be(40);
                    db.GetCollection("target").Count().Should().Be(41);
                    source.Query().Where("s = @0", Key(5)).GetPlan()["index"]["mode"].AsString.Should().StartWith("INDEX SEEK(s");
                    for (var key = 0; key < Keys; key++)
                        source.Count(Query.EQ("s", Key(key))).Should().Be(expected);
                    db.Commit().Should().BeTrue();
                }
                catch (Exception error)
                {
                    failure = error;
                    pinned.Set();
                    try { db.Rollback(); } catch { }
                }
            });
            owner.Start();
            pinned.Wait(TimeSpan.FromSeconds(30)).Should().BeTrue();
            int[] freed;
            try { freed = failure == null ? FreeIndexPages(db) : new int[0]; }
            finally { dropped.Set(); }
            owner.Join(TimeSpan.FromSeconds(30)).Should().BeTrue();
            failure.Should().BeNull();
            freed.Should().NotBeEmpty();

            Pages(db, "target", "Data").Concat(Pages(db, "target", "Index")).Intersect(freed).Should().NotBeEmpty();
            db.GetCollection("target").Count().Should().Be(41);
            db.GetCollection("source").Count().Should().Be(Rows);
        }
    }
}
