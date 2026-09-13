using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2129_Tests
    {
        [Fact]
        public void TestInsertAfterDeleteAll()
        {
            var db = new LiteDatabase(":memory:");
            var col = db.GetCollection<SwapChance>(nameof(SwapChance));
            col.EnsureIndex(x => x.Accounts1to2);
            col.EnsureIndex(x => x.Accounts2to1);

            col.InsertBulk(this.GenerateItems());
            col.DeleteAll();
            col.InsertBulk(this.GenerateItems());
        }

        private IEnumerable<SwapChance> GenerateItems()
        {
            var r = new Random();
            int seq = 1;
            return Enumerable.Range(0, 150).Select(x => new SwapChance
            {
                Rarity = "Uncommon",
                Template1Id = r.Next(15023),
                Template2Id = r.Next(142, 188645),
                Accounts1to2 = Enumerable.Range(0, 8).Select(a => Guid.NewGuid().ToString().Substring(0, 10) + ".wam").ToList(),
                Accounts2to1 = Enumerable.Range(0, 6).Select(a => Guid.NewGuid().ToString().Substring(0, 10) + ".wam").ToList(),
                Sequence = seq++
            });
        }
    }

    public class SwapChance
    {
        public ObjectId Id { get; set; }
        public int Sequence { get; set; } = 0;
        public string Rarity { get; set; } = string.Empty;
        public int Template1Id { get; set; }
        public int Template2Id { get; set; }
        public List<string> Accounts1to2 { get; set; } = new List<string>();
        public List<string> Accounts2to1 { get; set; } = new List<string>();
    }
}
