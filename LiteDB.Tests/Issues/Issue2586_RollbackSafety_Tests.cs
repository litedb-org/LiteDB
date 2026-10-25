using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2586_RollbackSafety_Tests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void RollbackAfterSafepoint_PreservesCommittedDataOnReopen(bool readOnlyMarker)
        {
            using var file = new TempFile();
            using (var engine = new LiteEngine(new EngineSettings { Filename = file.Filename }))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                db.CheckpointSize = 0;
                var docs = db.GetCollection("docs");
                docs.EnsureIndex("value");
                docs.Insert(new BsonDocument { ["_id"] = 1, ["value"] = "committed" });
                db.Checkpoint();

                db.BeginTrans().Should().BeTrue();
                docs.Update(new BsonDocument { ["_id"] = 1, ["value"] = "uncommitted" });
                docs.Insert(new BsonDocument { ["_id"] = 2, ["value"] = "uncommitted" });
                var transaction = engine.GetMonitor().GetThreadTransaction();
                transaction.Pages.TransactionSize.Should().BeGreaterThan(0);
                transaction.MaxTransactionSize = transaction.Pages.TransactionSize;
                transaction.Safepoint();
                transaction.Pages.TransactionSize.Should().Be(0);

                var page = transaction.Snapshots.Single().CollectionPage;
                page.IsDirty = true;
                var buffer = page.Buffer;
                try
                {
                    // Same legacy marker fault as #2586; normal cache transitions have
                    // separate coverage in Cache_Tests and Transactions_Tests.
                    if (readOnlyMarker) buffer.ShareCounter = 1;
                    db.Rollback().Should().BeTrue();
                }
                finally
                {
                    if (readOnlyMarker) buffer.ShareCounter = 0;
                }

                docs.FindById(1)["value"].AsString.Should().Be("committed");
                Assert.Null(docs.FindById(2));
                docs.Insert(new BsonDocument { ["_id"] = 3, ["value"] = "after rollback" });
            }

            using var reopened = new LiteDatabase(file.Filename);
            var rows = reopened.GetCollection("docs");
            rows.FindAll().Select(doc => doc["_id"].AsInt32).Should().BeEquivalentTo(new[] { 1, 3 });
            rows.Count(Query.EQ("value", "committed")).Should().Be(1);
            rows.Count(Query.EQ("value", "uncommitted")).Should().Be(0);
            rows.FindById(3)["value"].AsString.Should().Be("after rollback");
        }
    }
}
