using System;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public class RangeUnionSafety_Tests
    {
        [Theory]
        [InlineData("Score < 2 OR _id > 8")]
        [InlineData("Score < 2 OR Score > RANDOM()")]
        [InlineData("(Score < 2 AND Name = 'row') OR Score > 8")]
        [InlineData("(Values[*] ANY > 8 AND Values[*] ANY < 3) OR Values[*] ANY = 5")]
        [InlineData("Values[*] ALL < 3 OR Values[*] ALL > 8")]
        public void Unsupported_or_volatile_branches_retain_the_original_filter(string predicate)
        {
            using var db = CreateDatabase();
            var rows = db.GetCollection("rows");
            rows.Query().Where(predicate).GetPlan().ContainsKey("filters").Should().BeTrue();
            if (!predicate.Contains("RANDOM"))
            {
                var expression = BsonExpression.Create(predicate);
                var expected = rows.FindAll().Where(x => expression.ExecuteScalar(x).AsBoolean).Select(x => x["_id"]);
                rows.Find(predicate).Select(x => x["_id"]).Should().BeEquivalentTo(expected);
            }
        }

        [Theory]
        [InlineData("Score < 100 OR Score > SUBSTRING('x',1000)", false)]
        [InlineData("Score < 0 OR Score > SUBSTRING('x',1000)", true)]
        [InlineData("Score < 100 OR SUBSTRING(Name,1000) > 'z'", false)]
        [InlineData("Score < 0 OR SUBSTRING(Name,1000) > 'z'", true)]
        [InlineData("Score < 100 OR Score > (1 % @zero)", false)]
        [InlineData("Score < 0 OR Score > (1 % @zero)", true)]
        public void Throwing_expressions_keep_their_short_circuit_and_execution_time(string predicate, bool throws)
        {
            using var db = CreateDatabase();
            var query = db.GetCollection("rows").Query().Where(BsonExpression.Create(predicate,
                new BsonDocument { ["zero"] = 0 }));
            query.GetPlan().ContainsKey("filters").Should().BeTrue();
            if (throws)
            {
                Action execute = () => query.Count();
                execute.Should().Throw<Exception>();
            }
            else query.Count().Should().Be(10);
        }

        [Fact]
        public void Computed_index_keys_do_not_skip_document_expression_errors()
        {
            using var db = CreateDatabase();
            var rows = db.GetCollection("rows");
            rows.EnsureIndex("upper", "UPPER(Name)");
            var query = rows.Query().Where("UPPER(Name) < 'A' OR UPPER(Name) > 'Z'");
            query.GetPlan().ContainsKey("filters").Should().BeTrue();
            query.Count().Should().Be(0);
        }

        [Fact]
        public void Included_members_are_filtered_after_reference_resolution()
        {
            using var db = new LiteDatabase(":memory:");
            db.GetCollection("owners").Insert(new BsonDocument { ["_id"] = 1, ["Name"] = "even" });
            var rows = db.GetCollection("rows");
            rows.Insert(new BsonDocument { ["Owner"] = new BsonDocument { ["$id"] = 1, ["$ref"] = "owners" } });
            rows.EnsureIndex("owner_name", "Owner.Name");
            var query = rows.Query().Include("Owner").Where("(Owner.Name > 'a' AND Owner.Name < 'z') OR Owner.Name > 'zz'");
            query.GetPlan()["index"]["name"].AsString.Should().Be("_id");
            query.GetPlan().ContainsKey("filters").Should().BeTrue();
            query.ToArray().Single()["Owner"]["Name"].AsString.Should().Be("even");
        }

        [Fact]
        public void Large_disjunctions_fall_back_without_losing_matches()
        {
            using var db = CreateDatabase();
            var rows = db.GetCollection("rows");
            var predicate = string.Join(" OR ", Enumerable.Range(1, 80).Select(i => "(Score >= " + i + " AND Score <= " + i + ")"));
            var query = rows.Query().Where(predicate);
            query.GetPlan().ContainsKey("filters").Should().BeTrue();
            query.Count().Should().Be(10);
        }

        private static LiteDatabase CreateDatabase()
        {
            var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            rows.InsertBulk(Enumerable.Range(1, 10).Select(i => new BsonDocument
            {
                ["_id"] = i, ["Score"] = i, ["Name"] = "row", ["Values"] = new BsonArray(1, 10)
            }));
            rows.EnsureIndex("score", "Score");
            rows.EnsureIndex("values", "Values[*]");
            return db;
        }
    }
}
