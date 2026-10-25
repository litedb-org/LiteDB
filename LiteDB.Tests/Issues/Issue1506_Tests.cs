using System.Linq;
using FluentAssertions;
using FluentAssertions.Execution;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue1506_Tests
    {
        [Fact]
        public void Find_honors_query_limit_when_optional_paging_is_omitted()
        {
            using var db = new LiteDatabase(":memory:");
            var col = db.GetCollection("rows");
            col.Insert(new[]
            {
                Row(1, "keep", 30, "first", 101),
                Row(2, "keep", 20, "second", 102),
                Row(3, "keep", 10, "third", 103)
            });

            var query = Query.All("_id");
            query.Offset = 0;
            query.Limit = 1;

            var result = col.Find(query).ToArray();

            result.Should().ContainSingle();
            result[0]["_id"].AsInt32.Should().Be(1);
            result[0]["payload"].AsString.Should().Be("first");
            query.Offset.Should().Be(0);
            query.Limit.Should().Be(1);
        }

        [Fact]
        public void Find_paging_override_preserves_the_complete_reusable_query()
        {
            using var db = new LiteDatabase(":memory:");
            InsertCustomers(db);
            var col = db.GetCollection("rows");
            col.Insert(new[]
            {
                Row(1, "keep", 10, "last", 101),
                Row(2, "keep", 50, "first", 102),
                Row(3, "discard", 100, "filtered", 103),
                Row(4, "keep", 40, "second", 104),
                Row(5, "keep", 30, "third", 105),
                Row(6, "keep", 20, "fourth", 106)
            });

            var query = new Query
            {
                Select = BsonExpression.Create(
                    "{ rowId: $._id, token: $.payload, rank: $.rank, customer: $.customer.name }"),
                Offset = 1,
                Limit = 2
            };
            query.Includes.Add(BsonExpression.Create("$.customer"));
            query.Where.Add(BsonExpression.Create("$.kind = 'keep'"));
            query.OrderBy.Add(new QueryOrder(BsonExpression.Create("$.rank"), Query.Descending));
            var originalSql = query.ToSQL("rows");

            using (new AssertionScope())
            {
                var overridden = col.Find(query, skip: 2, limit: 2);
                AssertUnchanged(query, originalSql, "immediately after Find returns");

                using (var iterator = overridden.GetEnumerator())
                {
                    AssertUnchanged(query, originalSql, "after creating the lazy iterator");
                    iterator.MoveNext().Should().BeTrue();
                    AssertProjected(iterator.Current, 5, "third", 30, "customer 105");
                    AssertUnchanged(query, originalSql, "after partial enumeration");

                    iterator.MoveNext().Should().BeTrue();
                    AssertProjected(iterator.Current, 6, "fourth", 20, "customer 106");
                    iterator.MoveNext().Should().BeFalse();
                }

                AssertUnchanged(query, originalSql, "after full enumeration");

                var reused = col.Find(query).ToArray();
                reused.Should().HaveCount(2);
                AssertProjected(reused[0], 4, "second", 40, "customer 104");
                AssertProjected(reused[1], 5, "third", 30, "customer 105");
                AssertUnchanged(query, originalSql, "after reusing the caller's query");
                col.Count().Should().Be(6, "query execution must be read-only");
            }
        }

        private static BsonDocument Row(int id, string kind, int rank, string payload, int customerId)
        {
            return new BsonDocument
            {
                ["_id"] = id,
                ["kind"] = kind,
                ["rank"] = rank,
                ["payload"] = payload,
                ["customer"] = new BsonDocument
                {
                    ["$id"] = customerId,
                    ["$ref"] = "customers"
                }
            };
        }

        private static void InsertCustomers(LiteDatabase db)
        {
            var customers = db.GetCollection("customers");
            customers.Insert(Enumerable.Range(101, 6).Select(id => new BsonDocument
            {
                ["_id"] = id,
                ["name"] = "customer " + id
            }));
        }

        private static void AssertProjected(
            BsonDocument actual,
            int id,
            string token,
            int rank,
            string customer)
        {
            actual.Keys.Should().BeEquivalentTo("rowId", "token", "rank", "customer");
            actual["rowId"].AsInt32.Should().Be(id);
            actual["token"].AsString.Should().Be(token);
            actual["rank"].AsInt32.Should().Be(rank);
            actual["customer"].AsString.Should().Be(customer);
        }

        private static void AssertUnchanged(Query query, string originalSql, string phase)
        {
            query.Offset.Should().Be(1, phase);
            query.Limit.Should().Be(2, phase);
            query.Select.Source.Should().Be(
                "{rowId:$._id,token:$.payload,rank:$.rank,customer:$.customer.name}",
                phase);
            query.Includes.Select(x => x.Source).Should().Equal(new[] { "$.customer" }, phase);
            query.Where.Select(x => x.Source).Should().Equal(new[] { "$.kind=\"keep\"" }, phase);
            query.OrderBy.Should().ContainSingle();
            query.OrderBy[0].Expression.Source.Should().Be("$.rank", phase);
            query.OrderBy[0].Order.Should().Be(Query.Descending, phase);
            query.ToSQL("rows").Should().Be(originalSql, phase);
        }
    }
}
