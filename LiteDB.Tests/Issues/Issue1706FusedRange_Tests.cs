using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue1706FusedRange_Tests
    {
        private const string Collection = "rows";
        private const int DocumentCount = 5000;
        private const int Low = 1000;
        private const int High = 1200;

        public static IEnumerable<object[]> Shapes()
        {
            foreach (var field in new[] { "_id", "key" })
            foreach (var reversed in new[] { false, true })
            foreach (var order in new[] { Query.Ascending, Query.Descending })
            foreach (var parameters in new[] { false, true })
            {
                yield return new object[] { field, reversed, order, parameters };
            }
        }

        [Theory]
        [MemberData(nameof(Shapes))]
        public void Opposite_bounds_on_one_index_plan_a_single_bounded_range(string field, bool reversed, int order, bool parameters)
        {
            using var db = new LiteDatabase(":memory:");
            var rows = CreateRows(db, DocumentCount);

            var plan = rows.Query().Where(CreatePredicate(field, reversed, parameters)).OrderBy(field, order).GetPlan();

            plan["index"]["expr"].AsString.Should().Be("$." + field);
            plan["index"]["mode"].AsString.Should().Be($"INDEX RANGE SCAN({field} BETWEEN {Low} AND {High})");
            plan.ContainsKey("filters").Should().BeFalse("the range enforces both bounds exactly");
        }

        [Fact]
        public void Linq_range_without_order_or_limit_plans_the_same_bounded_range_for_both_operand_orders()
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection<Row>(Collection);
            rows.InsertBulk(Enumerable.Range(1, DocumentCount).Select(id => new Row { Id = id }));

            var first = rows.Query().Where(x => x.Id >= Low && x.Id <= High);
            var second = rows.Query().Where(x => x.Id <= High && x.Id >= Low);

            foreach (var query in new[] { first, second })
            {
                query.GetPlan()["index"]["mode"].AsString.Should().Be($"INDEX RANGE SCAN(_id BETWEEN {Low} AND {High})");
                query.ToArray().Select(x => x.Id).Should().Equal(Enumerable.Range(Low, High - Low + 1));
            }
        }

        [Theory]
        [MemberData(nameof(Shapes))]
        public void Bounded_range_visits_index_nodes_proportional_to_the_range(string field, bool reversed, int order, bool parameters)
        {
            using var engine = new LiteEngine();
            engine.Insert(Collection, Enumerable.Range(1, DocumentCount).Select(CreateDocument), BsonAutoId.Int32);
            engine.EnsureIndex(Collection, "key", "$.key", false);

            var query = new Query { Select = BsonExpression.Root };
            query.Where.Add(CreatePredicate(field, reversed, parameters));
            query.OrderBy.Add(new QueryOrder(BsonExpression.Create("$." + field), order));

            CountVisitedIndexNodes(engine, query).Should().Be(High - Low + 1);
        }

        [Theory]
        [InlineData(">", "<")]
        [InlineData(">=", "<")]
        [InlineData(">", "<=")]
        [InlineData(">=", "<=")]
        public void Inclusive_and_exclusive_edges_match_linq_to_objects_on_duplicate_keys(string lowerOperator, string upperOperator)
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection(Collection);
            var keys = Enumerable.Range(1, 120).Select(id => id / 3).ToArray();
            rows.Insert(keys.Select((key, index) => new BsonDocument { ["_id"] = index + 1, ["key"] = key }));
            rows.EnsureIndex("key");

            foreach (var bounds in new[] { (10, 30), (10, 10), (10, 11), (30, 10), (-5, 500), (0, 40) })
            foreach (var reversed in new[] { false, true })
            foreach (var order in new[] { Query.Ascending, Query.Descending })
            {
                var lower = $"key {lowerOperator} {bounds.Item1}";
                var upper = $"{bounds.Item2} {Flip(upperOperator)} key";
                var predicate = reversed ? upper + " AND " + lower : lower + " AND " + upper;
                var expected = keys
                    .Where(key => lowerOperator == ">" ? key > bounds.Item1 : key >= bounds.Item1)
                    .Where(key => upperOperator == "<" ? key < bounds.Item2 : key <= bounds.Item2)
                    .OrderBy(key => order * key);

                var query = rows.Query().Where(predicate).OrderBy("key", order);

                query.ToArray().Select(d => d["key"].AsInt32).Should().Equal(expected, predicate);
                query.GetPlan().ContainsKey("filters").Should().BeFalse(predicate);
            }
        }

        [Fact]
        public void Exclusive_bounds_are_shown_in_the_plan()
        {
            using var db = new LiteDatabase(":memory:");
            var rows = CreateRows(db, 100);

            rows.Query().Where("key < 30 AND key >= 10").GetPlan()["index"]["mode"].AsString
                .Should().Be("INDEX RANGE SCAN(key >= 10 AND key < 30)");
        }

        [Fact]
        public void Extra_range_terms_collapse_into_the_tightest_pair()
        {
            using var db = new LiteDatabase(":memory:");
            var rows = CreateRows(db, 100);

            var query = rows.Query().Where("key <= 40 AND key > 5 AND key < 30 AND key >= 10 AND key > 10");

            query.GetPlan()["index"]["mode"].AsString.Should().Be("INDEX RANGE SCAN(key > 10 AND key < 30)");
            query.GetPlan().ContainsKey("filters").Should().BeFalse();
            query.ToArray().Select(d => d["key"].AsInt32).Should().Equal(Enumerable.Range(11, 19));
        }

        [Fact]
        public void Terms_on_other_fields_stay_residual_filters()
        {
            using var db = new LiteDatabase(":memory:");
            var rows = CreateRows(db, 100);

            var query = rows.Query().Where("key <= 30 AND _id % 2 = 0 AND key >= 10");

            query.GetPlan()["filters"].AsArray.Select(x => x.AsString).Should().Equal("$._id%2=0");
            query.ToArray().Select(d => d["key"].AsInt32).Should().Equal(Enumerable.Range(10, 21).Where(x => x % 2 == 0));
        }

        [Fact]
        public void DateTime_and_string_keys_return_the_bounded_rows()
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection(Collection);
            var origin = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            rows.Insert(Enumerable.Range(1, 100).Select(id => new BsonDocument
            {
                ["_id"] = id,
                ["at"] = origin.AddDays(id),
                ["name"] = "n" + id.ToString("000")
            }));
            rows.EnsureIndex("at");
            rows.EnsureIndex("name");

            var byDate = BsonExpression.Create("at < @1 AND at >= @0", origin.AddDays(10), origin.AddDays(20));
            var byName = BsonExpression.Create("name <= @1 AND name > @0", "n010", "n020");

            rows.Find(byDate).Select(d => d["_id"].AsInt32).Should().Equal(Enumerable.Range(10, 10));
            rows.Find(byName).Select(d => d["_id"].AsInt32).Should().Equal(Enumerable.Range(11, 10));
            rows.Query().Where(byDate).GetPlan()["index"]["mode"].AsString.Should().StartWith("INDEX RANGE SCAN(at >= ");
            rows.Query().Where(byName).GetPlan()["index"]["mode"].AsString.Should().StartWith("INDEX RANGE SCAN(name > ");
        }

        [Fact]
        public void Mixed_type_and_inverted_bounds_follow_bson_ordering_without_throwing()
        {
            using var db = new LiteDatabase(":memory:");
            var rows = CreateRows(db, 20);

            foreach (var order in new[] { Query.Ascending, Query.Descending })
            {
                rows.Query().Where("key >= 'abc' AND key <= 5").OrderBy("key", order).ToArray().Should().BeEmpty();
                rows.Query().Where("key <= 5 AND key >= 'abc'").OrderBy("key", order).ToArray().Should().BeEmpty();
                rows.Query().Where("key >= 12 AND key <= 8").OrderBy("key", order).ToArray().Should().BeEmpty();
                rows.Query().Where("key BETWEEN 12 AND 8").OrderBy("key", order).ToArray().Should().BeEmpty();
                rows.Query().Where("key >= 10 AND key < 10").OrderBy("key", order).ToArray().Should().BeEmpty();

                // numbers sort before strings, so a string upper bound admits every number
                rows.Query().Where("key <= 'abc' AND key >= 15").OrderBy("key", order).ToArray()
                    .Select(d => d["key"].AsInt32).OrderBy(x => x).Should().Equal(Enumerable.Range(15, 6));
            }
        }

        public class Row
        {
            public int Id { get; set; }
        }

        private static ILiteCollection<BsonDocument> CreateRows(LiteDatabase db, int count)
        {
            var rows = db.GetCollection(Collection);
            rows.Insert(Enumerable.Range(1, count).Select(CreateDocument));
            rows.EnsureIndex("key");
            return rows;
        }

        private static BsonDocument CreateDocument(int id)
        {
            return new BsonDocument { ["_id"] = id, ["key"] = id };
        }

        private static BsonExpression CreatePredicate(string field, bool reversed, bool parameters)
        {
            var lower = field + " >= " + (parameters ? "@0" : Low.ToString());
            var upper = field + " <= " + (parameters ? "@1" : High.ToString());
            var source = reversed ? upper + " AND " + lower : lower + " AND " + upper;
            return parameters ? BsonExpression.Create(source, Low, High) : BsonExpression.Create(source);
        }

        private static string Flip(string upperOperator)
        {
            return upperOperator == "<" ? ">" : ">=";
        }

        private static int CountVisitedIndexNodes(LiteEngine engine, Query query)
        {
            var pragmas = new EnginePragmas(null);
            var monitor = engine.GetMonitor();
            var transaction = monitor.GetTransaction(true, true, out var isNew);

            try
            {
                var snapshot = transaction.CreateSnapshot(LockMode.Read, Collection, false);
                var plan = new QueryOptimization(snapshot, query, null, pragmas.Collation).ProcessQuery();
                var indexer = new IndexService(snapshot, pragmas.Collation, 1_000_000);

                return plan.Index.Run(snapshot.CollectionPage, indexer, query.ForUpdate).Count();
            }
            finally
            {
                if (isNew)
                {
                    monitor.ReleaseTransaction(transaction);
                }
            }
        }
    }
}
