using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public class CaseInsensitiveIndex_Tests
    {
        [Theory]
        [InlineData("score = 3")]
        [InlineData("3 = SCORE")]
        [InlineData("score >= 3 AND ScOrE < 6")]
        [InlineData("score IN [3,5] AND SCORE >= 4")]
        [InlineData("score = 3 OR SCORE = 5")]
        [InlineData("(score = 3 AND Label = 'keep') OR (SCORE = 3 AND Label = 'other')")]
        [InlineData("score != 3")]
        [InlineData("score BETWEEN 3 AND 5")]
        public void Root_field_casing_does_not_hide_scalar_indexes(string predicate)
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            rows.InsertBulk(Enumerable.Range(1, 40).Select(i => new BsonDocument
            {
                ["_id"] = i, ["Score"] = i % 10, ["Label"] = i % 2 == 0 ? "keep" : "other"
            }));
            var expected = rows.Find(predicate).Select(x => x["_id"].AsInt32).ToArray();
            rows.EnsureIndex("scores", "Score");
            var query = rows.Query().Where(predicate);
            query.GetPlan()["index"]["name"].AsString.Should().Be("scores");
            query.ToArray().Select(x => x["_id"].AsInt32).Should().BeEquivalentTo(expected);
            rows.Count(predicate).Should().Be(expected.Length);
        }

        [Fact]
        public void Ordering_grouping_and_preferred_projections_reuse_the_same_field_index()
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            rows.InsertBulk(Enumerable.Range(1, 40).Select(i => new BsonDocument { ["Score"] = i % 10 }));
            rows.EnsureIndex("scores", "Score");
            var ordered = rows.Query().OrderBy("sCoRe", Query.Descending).Select("{ value: score }");
            ordered.GetPlan()["index"]["name"].AsString.Should().Be("scores");
            ordered.GetPlan().ContainsKey("orderBy").Should().BeFalse();
            ordered.ToArray().Select(x => x["value"].AsInt32).Should().Equal(
                Enumerable.Range(0, 10).Reverse().SelectMany(i => Enumerable.Repeat(i, 4)));
            var preferred = rows.Query().Select("{ value: SCORE }");
            preferred.GetPlan()["index"]["name"].AsString.Should().Be("scores");
            preferred.ToArray().Select(x => x["value"].AsInt32).Should().Equal(
                Enumerable.Range(0, 10).SelectMany(i => Enumerable.Repeat(i, 4)));
            var grouped = rows.Query().GroupBy("score").Select("{ key: @key, n: COUNT(*) }");
            grouped.GetPlan()["index"]["name"].AsString.Should().Be("scores");
            grouped.GetPlan()["groupBy"].AsDocument.ContainsKey("orderBy").Should().BeFalse();
            var groups = grouped.ToArray();
            groups.Select(x => x["key"].AsInt32).Should().Equal(Enumerable.Range(0, 10));
            groups.Should().OnlyContain(x => x["n"].AsInt32 == 4);
        }

        [Theory]
        [InlineData("Score.Value", "score.value")]
        [InlineData("Tags[*]", "tags[*]")]
        [InlineData("quote\"FIELD", "QUOTE\"field")]
        [InlineData("odd FIELD", "ODD field")]
        public void Escaped_literal_field_names_keep_their_identity_when_case_changes(string field, string lookup)
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            rows.InsertBulk(Enumerable.Range(1, 3).Select(i => new BsonDocument { [field] = i }));
            rows.EnsureIndex("literal", x => x[field]);
            var query = rows.Query().Where(x => x[lookup] >= 2).Select(x => new { Value = x[lookup] });
            query.GetPlan()["index"]["name"].AsString.Should().Be("literal");
            query.ToArray().Select(x => x.Value.AsInt32).Should().Equal(2, 3);
        }

        [Theory]
        [InlineData("en-US/Ordinal")]
        [InlineData("tr-TR/IgnoreCase")]
        public void Field_identity_is_ordinal_and_applies_to_indexed_writes(string culture)
        {
            using var db = new LiteDatabase(new ConnectionString { Filename = ":memory:", Collation = new Collation(culture) });
            var rows = db.GetCollection("rows");
            rows.InsertBulk(Enumerable.Range(1, 10).Select(i => new BsonDocument { ["_id"] = i, ["Score"] = i }));
            rows.EnsureIndex("scores", "Score", true);
            rows.DeleteMany("sCoRe = 2").Should().Be(1);
            rows.UpdateMany("{ Score: SCORE + 100 }", "score = 3").Should().Be(1);
            rows.Find("score = 103").Single()["_id"].AsInt32.Should().Be(3);
            rows.Count("SCORE <= 10").Should().Be(8);
            var primary = rows.Query().Where("_ID = 3");
            primary.GetPlan()["index"]["mode"].AsString.Should().Contain("SEEK");
            primary.ToArray().Single()["Score"].AsInt32.Should().Be(103);
        }

        [Fact]
        public void Case_differences_in_computed_string_literals_do_not_alias_index_definitions()
        {
            using var db = new LiteDatabase(new ConnectionString { Filename = ":memory:", Collation = Collation.Binary });
            var rows = db.GetCollection("rows");
            rows.Insert(new BsonDocument { ["Name"] = "e" });
            rows.EnsureIndex("suffix", "Name + 'A'");
            var query = rows.Query().Where("Name + 'a' = 'ea'");
            query.GetPlan()["index"]["name"].AsString.Should().Be("_id");
            query.ToArray().Single()["Name"].AsString.Should().Be("e");
        }
    }
}
