using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public class IndexRangeBounds_Tests
    {
        [Theory]
        [InlineData("Score", "Score", false)]
        [InlineData("Score", "score", true)]
        [InlineData("Owner.Score", "owner.score", false)]
        [InlineData("Owner.Score", "owner.score", true)]
        public void Sentinel_equality_and_membership_never_return_skip_list_nodes(string field, string indexField, bool unique)
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            rows.InsertBulk(Enumerable.Range(1, 3).Select(i => new BsonDocument
            {
                ["_id"] = i, ["Score"] = i, ["Owner"] = new BsonDocument { ["Score"] = i }
            }));
            rows.EnsureIndex("score", indexField, unique);

            foreach (var sentinel in new[] { BsonValue.MinValue, BsonValue.MaxValue })
            {
                var parameters = new BsonDocument { ["value"] = sentinel, ["values"] = new BsonArray(sentinel, 2, sentinel) };
                var equality = BsonExpression.Create(field + " = @value", parameters);
                rows.Find(equality).Should().BeEmpty();
                rows.Count(equality).Should().Be(0);
                rows.Exists(equality).Should().BeFalse();

                var membership = BsonExpression.Create(field + " IN @values", parameters);
                rows.Count(membership).Should().Be(1);
                foreach (var order in new[] { Query.Ascending, Query.Descending })
                {
                    rows.Query().Where(membership).OrderBy(field, order).ToArray()
                        .Select(x => x["_id"].AsInt32).Should().Equal(2);
                }
                // Reach admission and resident SQL cache paths with both sentinels.
                for (var attempt = 0; attempt < 4; attempt++)
                {
                    using var reader = db.Execute("SELECT $ FROM rows WHERE " + field + " IN @values", parameters);
                    reader.ToArray().Select(x => x["_id"].AsInt32).Should().Equal(2);
                }
            }
        }

        [Theory]
        [InlineData("Score", "Score", "en-US/None")]
        [InlineData("Score", "score", "en-US/IgnoreCase")]
        [InlineData("Owner.Score", "owner.score", "en-US/None")]
        [InlineData("Owner.Score", "owner.score", "tr-TR/IgnoreCase")]
        public void Reversed_between_bounds_match_scalar_evaluation_with_reused_bindings(
            string field, string indexField, string culture)
        {
            var collation = new Collation(culture);
            using var db = new LiteDatabase(new ConnectionString { Filename = ":memory:", Collation = collation });
            var rows = db.GetCollection("rows");
            var values = new BsonValue[] { "A", "I", "I", "Z", 1, 2, 2, 3 };
            rows.InsertBulk(values.Select((value, i) => new BsonDocument
            {
                ["_id"] = i + 1,
                ["Score"] = value,
                ["Owner"] = new BsonDocument { ["Score"] = value }
            }));
            var documents = rows.FindAll().ToArray();
            rows.EnsureIndex("score", indexField);
            var template = BsonExpression.Create(field + " BETWEEN @lower AND @upper");
            var pairs = new[]
            {
                new BsonArray("A", "I"), new BsonArray("I", "A"),
                new BsonArray(1, 2), new BsonArray(2, 1),
                new BsonArray("I", "I"), new BsonArray(2, 2),
                new BsonArray("I", "A"), new BsonArray(2, 1)
            };

            foreach (var pair in pairs)
            {
                var parameters = new BsonDocument { ["lower"] = pair[0], ["upper"] = pair[1] };
                var predicate = template.Bind(parameters);
                var expected = documents.Where(x => predicate.ExecuteScalar(x, collation).AsBoolean)
                    .Select(x => x["_id"]).ToArray();
                foreach (var order in new[] { Query.Ascending, Query.Descending })
                {
                    var query = rows.Query().Where(predicate).OrderBy(field, order);
                    query.GetPlan()["index"]["name"].AsString.Should().Be("score");
                    query.ToArray().Select(x => x["_id"]).Should().BeEquivalentTo(expected);

                    var sql = "SELECT $ FROM rows WHERE " + field + " BETWEEN @lower AND @upper ORDER BY " +
                        field + (order == Query.Ascending ? " ASC" : " DESC");
                    using var reader = db.Execute(sql, parameters);
                    reader.ToArray().Select(x => x["_id"]).Should().BeEquivalentTo(expected);
                }
                rows.Count(predicate).Should().Be(expected.Length);
            }
        }
    }
}
