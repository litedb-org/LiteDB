using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public class GroupKeyAliasBinding_Tests
    {
        // Query.And/Or rename @key while composing and record the new name as a
        // group-key alias; a rebound copy must still receive the current group key.
        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void Rebound_composed_having_filters_still_receive_the_group_key(bool or, bool viaTemplate)
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            rows.Insert(new[] { 1, 1, 2 }.Select(category => new BsonDocument { ["Category"] = category }));

            var key = BsonExpression.Create("@key = 1");
            var other = BsonExpression.Create(or ? "COUNT(*) > 5" : "COUNT(*) > 0");
            var composed = or ? Query.Or(key, other) : Query.And(key, other);
            var rebound = (viaTemplate ? composed.WithoutParameters() : composed).Bind(new BsonDocument());

            Keys(rows, composed).Should().Equal(1);
            Keys(rows, rebound).Should().Equal(1);
        }

        [Fact]
        public void Bound_copies_own_their_group_key_aliases()
        {
            var composed = Query.And(BsonExpression.Create("@key = 1"), BsonExpression.Create("COUNT(*) > 0"));
            var rebound = composed.Bind(new BsonDocument());

            rebound.GroupKeyAliases.Should().BeEquivalentTo(composed.GroupKeyAliases);
            rebound.GroupKeyAliases.Should().NotBeSameAs(composed.GroupKeyAliases);
        }

        private static int[] Keys(ILiteCollection<BsonDocument> rows, BsonExpression having) =>
            rows.Query().GroupBy("$.Category").Having(having).Select("{ Key: @key }")
                .ToEnumerable().Select(x => x["Key"].AsInt32).OrderBy(x => x).ToArray();
    }
}
