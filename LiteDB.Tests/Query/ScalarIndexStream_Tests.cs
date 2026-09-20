using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public class ScalarIndexStream_Tests
    {
        [Theory]
        [InlineData("Score IN [2,2,3]")]
        [InlineData("Score = 2 OR Score = 3 OR Score = 2")]
        [InlineData("Score >= 2 AND Score < 4")]
        [InlineData("2 <= Score AND 4 > Score")]
        public void Scalar_indexes_keep_all_documents_sharing_a_key(string predicate)
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection<Row>();
            rows.InsertBulk(Enumerable.Range(1, 30).Select(i => new Row { Id = i, Score = i % 5 }));
            rows.EnsureIndex("score", x => x.Score);
            var query = rows.Query().Where(predicate).OrderBy(x => x.Score).ThenBy(x => x.Id);
            query.GetPlan()["index"]["name"].AsString.Should().Be("score");
            query.Count().Should().Be(12);
            query.ToArray().Select(x => x.Id).Should().Equal(Enumerable.Range(1, 30)
                .Where(i => i % 5 == 2 || i % 5 == 3).OrderBy(i => i % 5).ThenBy(i => i));
        }

        [Fact]
        public void Full_scalar_order_and_computed_predicates_keep_repeated_keys()
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection<Row>();
            rows.InsertBulk(Enumerable.Range(1, 30).Select(i => new Row { Id = i, Score = i % 5 }));
            rows.EnsureIndex("score", x => x.Score);
            rows.EnsureIndex("shifted", "Score + 1");
            var ordered = rows.Query().OrderBy(x => x.Score).Select(x => x.Score);
            ordered.GetPlan()["index"]["name"].AsString.Should().Be("score");
            ordered.ToArray().Should().Equal(Enumerable.Range(0, 5).SelectMany(i => Enumerable.Repeat(i, 6)));
            var computed = rows.Query().Where("Score + 1 = 3");
            computed.GetPlan()["index"]["name"].AsString.Should().Be("shifted");
            computed.Count().Should().Be(6);
        }

        [Theory]
        [InlineData("Tags[*] ANY IN [1,2,3]")]
        [InlineData("Tags[*] ANY >= 1")]
        public void Multikey_predicates_still_remove_repeated_document_addresses(string predicate)
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            rows.Insert(new BsonDocument { ["Tags"] = new BsonArray(1, 2) });
            rows.Insert(new BsonDocument { ["Tags"] = new BsonArray(2, 3) });
            rows.EnsureIndex("tags", "Tags[*]");
            var query = rows.Query().Where(predicate);
            query.GetPlan()["index"]["name"].AsString.Should().Be("tags");
            query.Count().Should().Be(2);
            query.ToArray().Length.Should().Be(2);
        }

        [Fact]
        public void Preferred_field_names_do_not_imply_a_scalar_index_expression()
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            rows.Insert(new BsonDocument { ["Tags"] = new BsonArray(1, 2), ["Tags[*]"] = 7 });
            rows.Insert(new BsonDocument { ["Tags"] = new BsonArray(2, 3), ["Tags[*]"] = 8 });
            rows.EnsureIndex("tags", "Tags[*]");
            var query = rows.Query().Select(x => new { Value = x["Tags[*]"] });
            query.GetPlan()["index"]["name"].AsString.Should().Be("_id");
            query.ToArray().Select(x => x.Value.AsInt32).Should().BeEquivalentTo(new[] { 7, 8 });
        }

        [Fact]
        public void Scalar_array_keys_can_be_repeated_between_documents()
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            rows.Insert(new BsonDocument { ["Values"] = new BsonArray(1, 2) });
            rows.Insert(new BsonDocument { ["Values"] = new BsonArray(1, 2) });
            rows.Insert(new BsonDocument { ["Values"] = new BsonArray(2, 3) });
            rows.EnsureIndex("values", "Values");
            rows.Query().Where("Values = [1,2] OR Values = [1,2]").Count().Should().Be(2);
        }

        public class Row
        {
            public int Id { get; set; }
            public int Score { get; set; }
        }
    }
}
