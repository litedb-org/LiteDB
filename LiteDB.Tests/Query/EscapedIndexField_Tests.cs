using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public class EscapedIndexField_Tests
    {
        [Theory]
        [InlineData("Tags[*]", "Tags[*]", false)]
        [InlineData("Tags[*]", "Tags[*]", true)]
        [InlineData("Nested.Value", "Nested.Value", false)]
        [InlineData("Nested.Value", "Nested.Value", true)]
        [InlineData("Score+1", "Score+1", false)]
        [InlineData("Score+1", "Score+1", true)]
        public void Literal_field_names_do_not_reuse_unrelated_path_or_computed_indexes(string field, string misleading, bool indexed)
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            rows.InsertBulk(Enumerable.Range(1, 2).Select(i => new BsonDocument
            {
                ["_id"] = i, ["Tags"] = new BsonArray(i, i + 1),
                ["Nested"] = new BsonDocument { ["Value"] = i }, ["Score"] = i,
                [field] = i + 6
            }));
            rows.EnsureIndex("misleading", misleading);
            if (indexed) rows.EnsureIndex("literal", x => x[field]);
            var query = rows.Query().Select(x => new { Value = x[field] });
            query.GetPlan()["index"]["name"].AsString.Should().Be(indexed ? "literal" : "_id");
            query.ToArray().Select(x => x.Value.AsInt32).Should().BeEquivalentTo(new[] { 7, 8 });
        }

        [Fact]
        public void A_literal_dollar_index_does_not_replace_whole_documents()
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            rows.Insert(new BsonDocument { ["_id"] = 1, ["$"] = 42, ["Payload"] = "kept" });
            rows.EnsureIndex("dollar", x => x["$"]);
            var document = rows.Query().ToArray().Single();
            document["_id"].AsInt32.Should().Be(1);
            document["Payload"].AsString.Should().Be("kept");
            document = rows.Query().OrderBy(x => x["$"]).ToArray().Single();
            document["_id"].AsInt32.Should().Be(1);
            document["Payload"].AsString.Should().Be("kept");
        }

        [Theory]
        [InlineData("odd field")]
        [InlineData("quote\"field")]
        [InlineData("back\\slash")]
        [InlineData("0")]
        [InlineData("emoji🙂")]
        public void Escaped_field_indexes_can_cover_projections(string field)
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            rows.Insert(new BsonDocument { [field] = 7 });
            rows.EnsureIndex("literal", x => x[field]);
            var query = rows.Query().Select(x => new { Value = x[field] });
            query.GetPlan()["index"]["name"].AsString.Should().Be("literal");
            query.ToArray().Single().Value.AsInt32.Should().Be(7);
        }
    }
}
