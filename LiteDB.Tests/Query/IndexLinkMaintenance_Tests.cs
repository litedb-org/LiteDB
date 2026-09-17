using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public class IndexLinkMaintenance_Tests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void Query_links_survive_index_changes_reopen_and_small_page_budgets(string password)
        {
            var filename = Path.GetTempFileName();
            var connection = new ConnectionString
            {
                Filename = filename, Password = password, CacheSize = 65536, TransactionPageLimit = 2
            };
            try
            {
                using (var db = new LiteDatabase(connection))
                {
                    var rows = db.GetCollection("rows", BsonAutoId.Int32);
                    rows.InsertBulk(Enumerable.Range(1, 2000).Select(i => Row(i, i % 11)));
                    rows.EnsureIndex("score", "Score");
                    rows.EnsureIndex("tags", "Tags[*]");
                    rows.EnsureIndex("unique", "Code", true);
                    for (var i = 1; i <= 100; i++) rows.Update(Row(i, 20));
                    rows.DeleteMany("_id >= 1900").Should().Be(101);
                    rows.Query().Where("Score = 20").Count().Should().Be(100);
                    db.Checkpoint();
                }
                using (var db = new LiteDatabase(connection))
                {
                    var rows = db.GetCollection("rows", BsonAutoId.Int32);
                    var generated = new BsonDocument { ["Score"] = 21, ["Code"] = 2100, ["Tags"] = new BsonArray(21, 22) };
                    rows.Insert(generated).AsInt32.Should().Be(1900);
                    rows.Count().Should().Be(1900);
                    rows.Query().Where("Score >= 0").OrderBy("Score", Query.Descending).ToArray()
                        .Select(x => x["Score"].AsInt32).Should().BeInDescendingOrder();
                    rows.Query().Where("Tags[*] ANY > 19").ToArray().Select(x => x["_id"].AsInt32)
                        .Should().BeEquivalentTo(Enumerable.Range(1, 100).Concat(new[] { 1900 }));
                    rows.Query().Where("Code >= 1").Count().Should().Be(1900);
                    rows.DeleteMany("Score = 20").Should().Be(100);
                    rows.Query().Where("Score = 20").ToArray().Should().BeEmpty();
                    rows.Query().Where("Score != 0").ToArray().Select(x => x["_id"].AsInt32)
                        .Should().BeEquivalentTo(Enumerable.Range(101, 1799).Where(i => i % 11 != 0).Concat(new[] { 1900 }));
                }
            }
            finally
            {
                File.Delete(filename);
                File.Delete(Path.Combine(Path.GetDirectoryName(filename), Path.GetFileNameWithoutExtension(filename) + "-log" + Path.GetExtension(filename)));
            }
        }

        private static BsonDocument Row(int id, int score) => new BsonDocument
        {
            ["_id"] = id, ["Score"] = score, ["Code"] = id, ["Tags"] = new BsonArray(score, score + 1)
        };
    }
}
