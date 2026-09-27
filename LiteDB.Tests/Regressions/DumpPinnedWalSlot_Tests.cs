using System;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Regressions
{
    /// <summary>
    /// Regression since 5.0.21: a transaction's unconfirmed WAL page is rewritten in place at its
    /// previous slot (e9bec08f8) after <c>MemoryCache.Invalidate(position, Log)</c>, which ENSUREs
    /// that the cached frame is idle. <c>$dump</c>/<c>$page_list</c> run in the thread's explicit
    /// transaction with a read snapshot that reads the transaction's own safepointed pages from
    /// those WAL slots and keeps them pinned until the snapshot is cleared - after the next
    /// safepoint/commit has already rewritten them. The ENSURE throws INVALID_DATAFILE_STATE,
    /// which stops the engine and marks the data file invalid. 5.0.21 appended every WAL page,
    /// so a pinned old slot stayed valid and the commit succeeded.
    /// </summary>
    [Trait("Category", "RegressionSince5021")]
    public class DumpPinnedWalSlot_Tests
    {
        [Fact]
        public void Dump_inside_an_explicit_transaction_does_not_break_the_next_commit()
        {
            using var file = new TempFile();
            var settings = new EngineSettings { Filename = file.Filename, TransactionPageLimit = 50 };

            using (var engine = new LiteEngine(settings))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                var col = db.GetCollection("docs");
                db.BeginTrans().Should().BeTrue();
                col.Insert(Enumerable.Range(0, 1000).Select(i => new BsonDocument { ["_id"] = i, ["payload"] = new string('x', 500) }));

                var transaction = engine.GetMonitor().GetThreadTransaction();
                var dirty = transaction.Pages.DirtyPages.Keys.Where(id => id != 0).Take(2).ToArray();
                dirty.Should().HaveCount(2, "a safepoint must have moved pages into unconfirmed WAL slots");

                foreach (var pageID in dirty)
                {
                    db.Execute($"SELECT $ FROM $dump({pageID})").ToEnumerable().ToList().Should().NotBeEmpty();
                }

                col.Update(Enumerable.Range(0, 1000).Select(i => new BsonDocument { ["_id"] = i, ["payload"] = new string('y', 500) }));
                db.Invoking(x => x.Commit()).Should().NotThrow();
            }

            using var reopened = new LiteDatabase(file.Filename);
            reopened.GetCollection("docs").Count(Query.EQ("payload", new string('y', 500))).Should().Be(1000);
        }
    }
}
