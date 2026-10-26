using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class AllocationOracle_Tests
    {
        [Theory]
        [InlineData("zero")]
        [InlineData("tail")]
        [InlineData("free")]
        [InlineData("orphan")]
        public void Independent_oracles_reject_broken_page_graphs_without_repairing_them(string mutation)
        {
            using var file = new TempFile();
            using (var db = AllocationWorkload.Open(file.Filename, null)) AllocationWorkload.Seed(db, "free");
            AllocationReachability.Verify(file.Filename, null);
            var bytes = File.ReadAllBytes(file.Filename);
            var header = new HeaderPage(new PageBuffer(bytes, 0, 0));
            if (mutation == "zero") Array.Clear(bytes, 8192, 8192);
            if (mutation == "tail") Buffer.BlockCopy(BitConverter.GetBytes((uint)(bytes.Length / 8192)), 0, bytes, 64, 4);
            if (mutation == "free") Buffer.BlockCopy(BitConverter.GetBytes(uint.MaxValue), 0, bytes, 60, 4);
            if (mutation == "orphan")
            {
                var root = new CollectionPage(new PageBuffer(bytes, (int)header.GetCollectionPageID("a") * 8192, 0));
                var index = root.GetCollectionIndexes().Single(i => i.Name == "key");
                var page = new IndexPage(new PageBuffer(bytes, (int)index.Head.PageID * 8192, 0));
                var head = page.GetIndexNodes().Single(n => n.Position == index.Head);
                // Keep all nodes/pages and ownership bytes, but bypass the indexed row.
                head.SetNext(0, index.Tail);
            }
            File.WriteAllBytes(file.Filename, bytes);
            Action inspect = () => AllocationReachability.Verify(file.Filename, null);
            inspect.Should().Throw<InvalidDataException>();
            File.ReadAllBytes(file.Filename).Should().Equal(bytes, "inspection must preserve its source");
        }
    }
}
