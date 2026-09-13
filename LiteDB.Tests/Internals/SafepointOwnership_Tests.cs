using System;
using System.IO;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Internals
{
    public class SafepointOwnership_Tests
    {
        [Fact]
        public void Safepoint_RetainedNode_WriteFailsOwnershipCheck()
        {
            using var engine = new LiteEngine(new EngineSettings
            {
                DataStream = new MemoryStream(),
                LogStream = new MemoryStream(),
                TransactionPageLimit = 1
            });

            engine.Insert("docs", new[] { new BsonDocument { ["_id"] = 1, ["value"] = "one" } }, BsonAutoId.Int32);
            engine.BeginTrans().Should().BeTrue();
            var transaction = engine.GetMonitor().GetThreadTransaction();
            var snapshot = transaction.CreateSnapshot(LockMode.Write, "docs", false);
            var indexer = new IndexService(snapshot, Collation.Binary, uint.MaxValue);
            var node = indexer.Find(snapshot.CollectionPage.PK, 1, false, Query.Ascending);

            node.Should().NotBeNull();
            transaction.Safepoint();

            Action mutateRetainedNode = () => node.SetNextNode(PageAddress.Empty);
            mutateRetainedNode.Should().Throw<LiteException>()
                .WithMessage("*buffer slice belongs*");

            engine.Rollback().Should().BeTrue();
        }
    }
}
