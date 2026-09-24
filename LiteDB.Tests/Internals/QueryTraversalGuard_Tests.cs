using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Internals
{
    public class QueryTraversalGuard_Tests
    {
        [Theory]
        [InlineData("seek", Query.Ascending)]
        [InlineData("seek", Query.Descending)]
        [InlineData("scan", Query.Ascending)]
        [InlineData("scan", Query.Descending)]
        [InlineData("exclusion", Query.Ascending)]
        [InlineData("exclusion", Query.Descending)]
        public void Query_traversal_keeps_its_guard_and_formatted_error(string traversal, int order)
        {
            using var engine = new LiteEngine(new EngineSettings
            {
                DataStream = new MemoryStream(), LogStream = new MemoryStream()
            });
            engine.Insert("rows", new[] { new BsonDocument { ["_id"] = 1 } }, BsonAutoId.Int32);
            engine.BeginTrans().Should().BeTrue();
            var transaction = engine.GetMonitor().GetThreadTransaction();
            var snapshot = transaction.CreateSnapshot(LockMode.Read, "rows", false);
            var index = snapshot.CollectionPage.PK;
            // A zero traversal budget deterministically exercises the same guard
            // used to stop corrupt/cyclic index chains, without corrupting a file.
            var indexer = new IndexService(snapshot, Collation.Binary, 0);
            Action execute = traversal == "seek" ? () => indexer.Find(index, 1, true, order) :
                traversal == "scan" ? () => indexer.FindAll(index, order).ToArray() :
                () => new IndexNotEquals("_id", 0, order).Execute(indexer, index).ToArray();
            execute.Should().Throw<LiteException>().WithMessage("*Detected loop*(_id*");
            engine.Rollback().Should().BeTrue();
        }

        [Theory]
        [InlineData(Query.Ascending)]
        [InlineData(Query.Descending)]
        public void Exclusion_scan_includes_the_transaction_page_allowance(int order)
        {
            using var engine = new LiteEngine(new EngineSettings
            {
                DataStream = new MemoryStream(), LogStream = new MemoryStream()
            });
            engine.Insert("rows", new[] { new BsonDocument { ["_id"] = 1 } }, BsonAutoId.Int32);
            engine.BeginTrans().Should().BeTrue();
            var transaction = engine.GetMonitor().GetThreadTransaction();
            var snapshot = transaction.CreateSnapshot(LockMode.Write, "rows", false);
            snapshot.NewPage<IndexPage>();
            var index = snapshot.CollectionPage.PK;
            var indexer = new IndexService(snapshot, Collation.Binary, 0);
            new IndexNotEquals("_id", 0, order).Execute(indexer, index)
                .Select(x => x.Key.AsInt32).Should().Equal(1);
            engine.Rollback().Should().BeTrue();
        }

        [Fact]
        public void Cursor_data_read_includes_the_transaction_page_allowance()
        {
            using var engine = new LiteEngine(new EngineSettings
            {
                DataStream = new MemoryStream(), LogStream = new MemoryStream()
            });
            engine.Insert("rows", new[] { new BsonDocument { ["_id"] = 1 } }, BsonAutoId.Int32);
            engine.BeginTrans().Should().BeTrue();
            var transaction = engine.GetMonitor().GetThreadTransaction();
            var snapshot = transaction.CreateSnapshot(LockMode.Write, "rows", false);
            var data = new DataService(snapshot, 0);
            var address = data.Insert(new BsonDocument
            {
                ["payload"] = new string('x', DataService.MAX_DATA_BYTES_PER_PAGE * 2)
            });
            ulong counter = 0;
            var blocks = 0;

            while (data.TryRead(ref address, ref counter, out _)) blocks++;

            blocks.Should().BeGreaterThan(1);
            engine.Rollback().Should().BeTrue();
        }
    }
}
