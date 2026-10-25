using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public class NestedIndexIdentity_Tests
    {
        [Theory]
        [InlineData("owner.score = 3")]
        [InlineData("3 = OWNER.SCORE")]
        [InlineData("owner.score >= 3 AND OWNER.Score < 6")]
        [InlineData("owner.score IN [3,5] AND OWNER.SCORE >= 4")]
        [InlineData("owner.score = 3 OR OWNER.SCORE = 5")]
        [InlineData("Owner.score BETWEEN 3 AND 5")]
        [InlineData("owner.score != 3")]
        [InlineData("owner.score < 5 OR OWNER.SCORE > 7")]
        [InlineData("(owner.score >= 2 AND (owner.score < 4 OR OWNER.SCORE >= 8)) OR owner.score = 1")]
        [InlineData("(owner.score = 3 AND Label = 'keep') OR (OWNER.Score = 3 AND Label = 'other')")]
        [InlineData("owner.score = null")]
        public void Member_casing_does_not_hide_nested_scalar_indexes(string predicate)
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            rows.InsertBulk(Enumerable.Range(1, 40).Select(i => new BsonDocument
            {
                ["_id"] = i, [i % 2 == 0 ? "Owner" : "OWNER"] = i % 7 == 0 ? BsonValue.Null :
                    i % 11 == 0 ? new BsonValue(37) : i % 13 == 0 ? new BsonArray(new BsonDocument { ["Score"] = 3 }) :
                    new BsonDocument { [i % 2 == 0 ? "Score" : "SCORE"] = i % 10 },
                ["Label"] = i % 2 == 0 ? "keep" : "other"
            }));
            var expected = rows.Find(predicate).Select(x => x["_id"]).ToArray();
            rows.EnsureIndex("nested", "Owner.Score");
            var query = rows.Query().Where(predicate);
            query.GetPlan()["index"]["name"].AsString.Should().Be("nested");
            query.ToArray().Select(x => x["_id"]).Should().BeEquivalentTo(expected);
            rows.Count(predicate).Should().Be(expected.Length);
        }

        [Fact]
        public void Ordinary_linq_matches_lowercase_index_definitions_with_current_bindings()
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection<Row>("rows");
            rows.InsertBulk(Enumerable.Range(1, 20).Select(i => new Row { Id = i, Owner = new Owner { Score = i } }));
            rows.EnsureIndex("nested", "owner.score");
            for (var low = 3; low < 6; low++)
            {
                var high = low + 3;
                var query = rows.Query().Where(x => x.Owner.Score >= low && x.Owner.Score < high)
                    .OrderByDescending(x => x.Owner.Score).Select(x => new { x.Id, x.Owner.Score });
                query.GetPlan()["index"]["name"].AsString.Should().Be("nested");
                query.GetPlan().ContainsKey("filters").Should().BeFalse();
                query.GetPlan().ContainsKey("orderBy").Should().BeFalse();
                query.ToArray().Select(x => x.Score).Should().Equal(low + 2, low + 1, low);
                var arithmetic = rows.Query().Where(x => x.Owner.Score >= low && x.Owner.Score < low + 3);
                arithmetic.GetPlan()["index"]["name"].AsString.Should().Be("nested");
                arithmetic.ToArray().Select(x => x.Owner.Score).Should().Equal(low, low + 1, low + 2);
            }
            var keys = new[] { 3, 5, 7 };
            var membership = rows.Query().Where(x => keys.Contains(x.Owner.Score) || x.Owner.Score > 18);
            membership.GetPlan().ContainsKey("filters").Should().BeFalse();
            membership.ToArray().Select(x => x.Owner.Score).Should().Equal(3, 5, 7, 19, 20);
        }

        [Fact]
        public void Ordering_and_grouping_keep_nested_document_lookups()
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            rows.InsertBulk(Enumerable.Range(1, 40).Select(i => new BsonDocument { ["Owner"] = new BsonDocument { ["Score"] = i % 10 } }));
            rows.EnsureIndex("nested", "Owner.Score");
            var ordered = rows.Query().OrderBy("owner.score", Query.Descending).Select("{ value: OWNER.SCORE }");
            ordered.GetPlan()["index"]["name"].AsString.Should().Be("nested");
            ordered.GetPlan().ContainsKey("orderBy").Should().BeFalse();
            ordered.GetPlan()["lookup"]["loader"].AsString.Should().Be("document");
            ordered.Offset(3).Limit(7).ToArray().Select(x => x["value"].AsInt32).Should().Equal(9, 8, 8, 8, 8, 7, 7);
            var grouped = rows.Query().GroupBy("OWNER.score").Select("{ key: @key, n: COUNT(*) }");
            grouped.GetPlan()["index"]["name"].AsString.Should().Be("nested");
            grouped.GetPlan()["groupBy"].AsDocument.ContainsKey("orderBy").Should().BeFalse();
            var groups = grouped.ToArray();
            groups.Select(x => x["key"].AsInt32).Should().Equal(Enumerable.Range(0, 10));
            groups.Select(x => x["n"].AsInt32).Should().OnlyContain(x => x == 4);
        }

        [Fact]
        public void Existing_templates_use_current_indexes_and_fresh_parameter_documents()
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            rows.InsertBulk(Enumerable.Range(1, 10).Select(i => new BsonDocument { ["_id"] = i, ["Owner"] = new BsonDocument { ["Score"] = i } }));
            var template = BsonExpression.Create("owner.score >= @low AND OWNER.SCORE < @high", new BsonDocument { ["low"] = 3, ["high"] = 5 });
            for (var round = 0; round < 3; round++)
            {
                if (round == 1) rows.EnsureIndex("nested", "Owner.Score");
                if (round == 2) rows.DropIndex("nested");
                rows.Query().Where(template).GetPlan()["index"]["name"].AsString.Should().Be(round == 1 ? "nested" : "_id");
                rows.Find(template).Select(x => x["_id"].AsInt32).Should().Equal(3, 4);
                rows.Find(template.Bind(new BsonDocument { ["low"] = 7, ["high"] = 9 })).Select(x => x["_id"].AsInt32).Should().Equal(7, 8);
            }
        }

        public class Row
        {
            public int Id { get; set; }
            public Owner Owner { get; set; }
        }

        public class Owner
        {
            public int Score { get; set; }
        }
    }
}
