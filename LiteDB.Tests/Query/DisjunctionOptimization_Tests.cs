using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public class DisjunctionOptimization_Tests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Same_field_equalities_seek_in_index_order(bool descending)
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection<Row>("rows");
            rows.InsertBulk(Enumerable.Range(1, 20).Select(i => new Row { Id = i, Score = i, Name = "N" + i }));
            rows.EnsureIndex(x => x.Score);
            var a = 17;
            var b = 3;
            var query = rows.Query().Where(x => x.Score == a || b == x.Score || x.Score == 10 || x.Score == a);
            if (descending) query.OrderByDescending(x => x.Score);
            else query.OrderBy(x => x.Score);
            query.GetPlan()["index"]["mode"].AsString.Should().Contain(" IN ");
            query.GetPlan().ContainsKey("orderBy").Should().BeFalse();
            query.Offset(1).Limit(2).ToArray().Select(x => x.Score).Should().Equal(descending ? new[] { 10, 3 } : new[] { 10, 17 });
            a = 19;
            rows.Query().Where(x => x.Score == a || x.Score == b).ToArray().Select(x => x.Score).Should().BeEquivalentTo(new[] { 19, 3 });
        }

        [Theory]
        [InlineData("Name = 'B' OR Name = 'a' OR Name = 'A' OR Name = null", "name")]
        [InlineData("UPPER(Name) = 'B' OR UPPER(Name) = 'A'", "upper")]
        public void Sql_uses_expression_indexes_and_collation_without_duplicates(string predicate, string index)
        {
            using var db = new LiteDatabase(new ConnectionString { Filename = ":memory:", Collation = new Collation("en-US/IgnoreCase") });
            var rows = db.GetCollection<Row>("rows");
            rows.InsertBulk(new[] { new Row { Id = 1, Name = "a" }, new Row { Id = 2, Name = "B" }, new Row { Id = 3 } });
            rows.EnsureIndex("name", "Name");
            rows.EnsureIndex("upper", "UPPER(Name)");
            var query = rows.Query().Where(predicate).OrderBy("Name");
            query.GetPlan()["index"]["name"].AsString.Should().Be(index);
            query.ToArray().Select(x => x.Id).Should().Equal(index == "name" ? new[] { 3, 1, 2 } : new[] { 1, 2 });
            query.Count().Should().Be(index == "name" ? 3 : 2);
        }

        [Theory]
        [InlineData("Score = 2 OR _id = 3")]
        [InlineData("Scores ANY = 2 OR Scores ANY = 3")]
        public void Incompatible_or_multikey_disjunctions_keep_the_filter(string predicate)
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection<Row>("rows");
            rows.Insert(new Row { Id = 1 });
            rows.EnsureIndex(x => x.Score);
            rows.EnsureIndex("scores", "Scores[*]");
            rows.Query().Where(predicate).GetPlan().ContainsKey("filters").Should().BeTrue();
        }

        public class Row
        {
            public int Id { get; set; }
            public int Score { get; set; }
            public string Name { get; set; }
        }
    }
}
