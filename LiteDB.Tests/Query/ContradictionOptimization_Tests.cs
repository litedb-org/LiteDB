using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public class ContradictionOptimization_Tests
    {
        [Theory]
        [InlineData("Score > 7 AND Score < 3")]
        [InlineData("Score >= 3 AND Score < 3")]
        [InlineData("Score > 3 AND Score <= 3")]
        [InlineData("Score = 3 AND Score = 7")]
        public void Impossible_scalar_constraints_use_an_empty_input(string predicate)
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            rows.Insert(new BsonDocument { ["Score"] = 5, ["Name"] = "a" });
            rows.EnsureIndex("score", "Score");
            var query = rows.Query().Where(predicate);
            query.GetPlan()["index"]["mode"].AsString.Should().StartWith("EMPTY");
            query.ToArray().Should().BeEmpty();
            query.Count().Should().Be(0);
            query.Exists().Should().BeFalse();
            using var aggregate = db.Execute("SELECT COUNT(*) AS n FROM rows WHERE " + predicate);
            aggregate.ToEnumerable().Single()["n"].AsInt32.Should().Be(0);
            using var group = db.Execute("SELECT Name, COUNT(*) AS n FROM rows WHERE " + predicate + " GROUP BY Name");
            group.ToEnumerable().Should().BeEmpty();
        }

        [Theory]
        [InlineData("Name = 'a' AND Name = 'b'")]
        [InlineData("Name = null AND Name = 'a'")]
        public void Impossible_unindexed_constraints_return_no_rows(string predicate)
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            rows.Insert(new BsonDocument { ["Score"] = 5, ["Name"] = "a" });
            rows.EnsureIndex("score", "Score");
            var query = rows.Query().Where(predicate);
            query.ToArray().Should().BeEmpty();
            query.Count().Should().Be(0);
            query.Exists().Should().BeFalse();
            using var aggregate = db.Execute("SELECT COUNT(*) AS n FROM rows WHERE " + predicate);
            aggregate.ToEnumerable().Single()["n"].AsInt32.Should().Be(0);
            using var group = db.Execute("SELECT Name, COUNT(*) AS n FROM rows WHERE " + predicate + " GROUP BY Name");
            group.ToEnumerable().Should().BeEmpty();
        }

        [Fact]
        public void Rebound_and_separate_parameter_documents_are_checked_each_time()
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection<RangeOptimization_Tests.Row>("rows");
            rows.Insert(new RangeOptimization_Tests.Row { Id = 1, Score = 5 });
            var low = 7;
            var high = 3;
            rows.Query().Where(x => x.Score > low).Where(x => x.Score < high).Count().Should().Be(0);
            var template = db.Mapper.GetExpression<RangeOptimization_Tests.Row, bool>(x => x.Score > low && x.Score < high);
            rows.Query().Where(template).ToArray().Should().BeEmpty();
            var rebound = template.Bind(new BsonDocument { ["p0"] = 3, ["p1"] = 7 });
            rows.Query().Where(rebound).ToArray().Select(x => x.Id).Should().Equal(1);
        }

        [Theory]
        [InlineData("Name = 'a' AND Name = 'A'")]
        [InlineData("Score >= 5 AND Score <= 5.0")]
        [InlineData("Values[*] ANY > 8 AND Values[*] ANY < 3")]
        [InlineData("Name = 'a' OR Name = 'b'")]
        public void Satisfiable_collated_numeric_and_multikey_constraints_are_not_pruned(string predicate)
        {
            using var db = new LiteDatabase(new ConnectionString { Filename = ":memory:", Collation = new Collation("en-US/IgnoreCase") });
            var rows = db.GetCollection("rows");
            rows.Insert(new BsonDocument { ["Score"] = 5, ["Name"] = "A", ["Values"] = new BsonArray(1, 10) });
            rows.Query().Where(predicate).Count().Should().Be(1);
        }
    }
}
