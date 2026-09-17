using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Internals
{
    public class IndexNodeLinks_Tests
    {
        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(5)]
        [InlineData(32)]
        public void Links_round_trip_at_every_level_and_reloaded_views_own_their_values(byte levels)
        {
            var buffer = new PageBuffer(new byte[Constants.PAGE_SIZE + 11], 11, 1);
            buffer.ShareCounter = Constants.BUFFER_WRITABLE;
            var page = new IndexPage(buffer, 1);
            var node = page.InsertIndexNode(0, levels, 123, new PageAddress(31, 7), IndexNode.GetNodeLength(levels, 123, out _));
            for (byte i = 0; i < levels; i++)
            {
                node.GetNextPrev(i, Query.Ascending).Should().Be(PageAddress.Empty);
                node.GetNextPrev(i, Query.Descending).Should().Be(PageAddress.Empty);
                node.SetPrev(i, new PageAddress(uint.MaxValue - i, (byte)(i + 1)));
                node.SetNext(i, new PageAddress(0x01020304u + i, (byte)(i * 3)));
            }
            var loaded = page.GetIndexNode(node.Position.Index);
            for (byte i = 0; i < levels; i++)
            {
                loaded.GetNextPrev(i, Query.Ascending).Should().Be(new PageAddress(0x01020304u + i, (byte)(i * 3)));
                loaded.GetNextPrev(i, Query.Descending).Should().Be(new PageAddress(uint.MaxValue - i, (byte)(i + 1)));
            }
            node.SetNext(0, new PageAddress(555, 4));
            loaded.GetNextPrev(0, Query.Ascending).Should().Be(new PageAddress(0x01020304u, 0));
            Action invalidLevel = () => node.SetNext(levels, new PageAddress(99, 99));
            invalidLevel.Should().Throw<Exception>();
            var updated = page.GetIndexNode(node.Position.Index);
            updated.GetNextPrev(0, Query.Ascending).Should().Be(new PageAddress(555, 4));
            updated.DataBlock.Should().Be(new PageAddress(31, 7));
            updated.Key.AsInt32.Should().Be(123);
            buffer.ShareCounter = 0;
        }

        [Fact]
        public void Node_and_head_links_remain_readable_after_page_release()
        {
            using var engine = new LiteEngine(new EngineSettings
            {
                DataStream = new MemoryStream(), LogStream = new MemoryStream(), TransactionPageLimit = 1
            });
            engine.Insert("rows", Enumerable.Range(1, 50).Select(i => new BsonDocument { ["_id"] = i }), BsonAutoId.Int32);
            engine.BeginTrans().Should().BeTrue();
            var transaction = engine.GetMonitor().GetThreadTransaction();
            var snapshot = transaction.CreateSnapshot(LockMode.Write, "rows", false);
            var indexer = new IndexService(snapshot, Collation.Binary, uint.MaxValue);
            var nodes = new[] { indexer.GetNode(snapshot.CollectionPage.PK.Head), indexer.Find(snapshot.CollectionPage.PK, 25, false, Query.Ascending) };
            var links = nodes.Select(node => Enumerable.Range(0, node.Levels).SelectMany(i => new[]
            {
                node.GetNextPrev((byte)i, Query.Ascending), node.GetNextPrev((byte)i, Query.Descending)
            }).ToArray()).ToArray();
            transaction.Safepoint();
            for (var n = 0; n < nodes.Length; n++)
            {
                for (byte i = 0; i < nodes[n].Levels; i++)
                {
                    nodes[n].GetNextPrev(i, Query.Ascending).Should().Be(links[n][i * 2]);
                    nodes[n].GetNextPrev(i, Query.Descending).Should().Be(links[n][i * 2 + 1]);
                }
            }
            Action write = () => nodes[1].SetNext(0, PageAddress.Empty);
            write.Should().Throw<LiteException>().WithMessage("*buffer slice belongs*");
            engine.Rollback().Should().BeTrue();
        }
    }
}
