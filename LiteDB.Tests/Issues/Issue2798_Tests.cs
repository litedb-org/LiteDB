using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2798_Tests
    {
        public interface IRow { string Key { get; set; } int Value { get; set; } }
        public class Row : IRow
        {
            [BsonId] public string Key { get; set; }
            public int Value { get; set; }
        }

        [Fact]
        public void Interface_queries_and_deletes_use_the_same_id_mapping_as_insert()
        {
            using var db = new LiteDatabase(":memory:", new BsonMapper());
            var col = db.GetCollection<IRow>("rows");
            col.Insert(new Row { Key = "alpha", Value = 11 });
            col.Insert(new Row { Key = "beta", Value = 22 });
            col.FindById("alpha").Value.Should().Be(11);
            db.GetCollection("rows").FindById("alpha")["_id"].AsString.Should().Be("alpha");
            col.Find(x => x.Key == "alpha").Select(x => x.Value).Should().Equal(11);
            col.Query().Where(x => x.Key == "beta").ToArray().Select(x => x.Value).Should().Equal(22);
            col.DeleteMany(x => x.Key == "alpha").Should().Be(1);
            col.FindAll().Select(x => x.Key).Should().Equal("beta");
            db.GetCollection("rows").FindAll().Select(x => x["Value"].AsInt32).Should().Equal(22);
        }
    }
}
