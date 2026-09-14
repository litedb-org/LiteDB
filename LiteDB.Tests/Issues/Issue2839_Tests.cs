#if NET8_0_OR_GREATER
using System;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2839_Tests
    {
        private const string CountQueryRoute = "count-query-route";
        private const string LongCountQueryRoute = "long-count-query-route";
        private const string UnfilteredRoute = "unfiltered-route";

        private const long CountQueryResult = 1_234_567_890L;
        private const long LongCountQueryResult = 4_294_967_303L;
        private const long UnfilteredLongCountResult = (long)int.MaxValue + 101L;

        // Return a different engine value for every query shape. This keeps an
        // implementation from making LongCount(Query) green by ignoring its Query.
        public class CountEngine : DispatchProxy
        {
            public string ExpectedRoute;
            public int Queries;

            protected override object Invoke(MethodInfo method, object[] args)
            {
                if (method.Name == nameof(IDisposable.Dispose)) return null;

                if (method.Name == nameof(ILiteEngine.Query))
                {
                    ((string)args[0]).Should().Be("rows");

                    var query = (Query)args[1];
                    var route = GetRouteAndValidateQuery(query);

                    route.Should().Be(ExpectedRoute);
                    Queries++;

                    return new BsonDataReader(new BsonDocument
                    {
                        ["count"] = ResultFor(route)
                    });
                }

                throw new InvalidOperationException("Unexpected engine call: " + method.Name);
            }

            private static string GetRouteAndValidateQuery(Query query)
            {
                query.Select.Source.Should().Be(
                    BsonExpression.Create("{ count: COUNT(*._id) }").Source);
                query.Includes.Should().BeEmpty();
                query.GroupBy.Should().BeNull();
                query.Having.Should().BeNull();
                query.ForUpdate.Should().BeFalse();
                query.ExplainPlan.Should().BeFalse();

                if (query.Where.Count == 0)
                {
                    query.OrderBy.Should().BeEmpty();
                    query.Offset.Should().Be(0);
                    query.Limit.Should().Be(int.MaxValue);
                    return UnfilteredRoute;
                }

                query.Where.Should().ContainSingle();
                query.OrderBy.Should().ContainSingle();
                query.OrderBy.Single().Expression.Source.Should().Be("$.sequence");
                query.OrderBy.Single().Order.Should().Be(Query.Descending);
                query.Offset.Should().Be(17);
                query.Limit.Should().Be(23);

                var predicate = query.Where.Single();
                predicate.Source.Should().Be(
                    BsonExpression.Create("$.route = @route", new BsonDocument
                    {
                        ["route"] = "source-format-only"
                    }).Source);
                predicate.Parameters.Keys.Should().Equal("route");

                return predicate.Parameters["route"].AsString;
            }

            private static long ResultFor(string route)
            {
                switch (route)
                {
                    case CountQueryRoute: return CountQueryResult;
                    case LongCountQueryRoute: return LongCountQueryResult;
                    case UnfilteredRoute: return UnfilteredLongCountResult;
                    default: throw new InvalidOperationException("Unexpected query route: " + route);
                }
            }
        }

        [Fact]
        public void Count_Query_routes_the_exact_query_and_reads_an_Int32_result()
        {
            var query = CreateRoutedQuery(CountQueryRoute);
            var originalSelect = query.Select;
            var engine = CreateEngine(CountQueryRoute, out var fake);

            using var db = new LiteDatabase(engine);

            db.GetCollection("rows").Count(query).Should().Be((int)CountQueryResult);
            fake.Queries.Should().Be(1);
            query.Select.Should().BeSameAs(originalSelect);
            AssertRoutedQueryWasNotMutated(query, CountQueryRoute);
        }

        [Fact]
        public void LongCount_without_Query_preserves_an_engine_result_above_Int32()
        {
            var engine = CreateEngine(UnfilteredRoute, out var fake);

            using var db = new LiteDatabase(engine);

            db.GetCollection("rows").LongCount().Should().Be(UnfilteredLongCountResult);
            fake.Queries.Should().Be(1);
        }

        [Fact]
        public void LongCount_Query_routes_the_exact_query_and_preserves_Int64_width()
        {
            var query = CreateRoutedQuery(LongCountQueryRoute);
            var originalSelect = query.Select;
            var engine = CreateEngine(LongCountQueryRoute, out var fake);

            using var db = new LiteDatabase(engine);

            db.GetCollection("rows").LongCount(query).Should().Be(LongCountQueryResult);
            fake.Queries.Should().Be(1);
            query.Select.Should().BeSameAs(originalSelect);
            AssertRoutedQueryWasNotMutated(query, LongCountQueryRoute);
        }

        private static ILiteEngine CreateEngine(string expectedRoute, out CountEngine fake)
        {
            var engine = DispatchProxy.Create<ILiteEngine, CountEngine>();
            fake = (CountEngine)(object)engine;
            fake.ExpectedRoute = expectedRoute;
            return engine;
        }

        private static Query CreateRoutedQuery(string route)
        {
            var query = Query.All("$.sequence", Query.Descending);
            query.Where.Add(BsonExpression.Create("$.route = @route", new BsonDocument
            {
                ["route"] = route
            }));
            query.Offset = 17;
            query.Limit = 23;
            return query;
        }

        private static void AssertRoutedQueryWasNotMutated(Query query, string route)
        {
            query.Select.Should().BeSameAs(BsonExpression.Root);
            query.Where.Should().ContainSingle();
            query.Where.Single().Parameters["route"].AsString.Should().Be(route);
            query.OrderBy.Should().ContainSingle();
            query.OrderBy.Single().Expression.Source.Should().Be("$.sequence");
            query.OrderBy.Single().Order.Should().Be(Query.Descending);
            query.Offset.Should().Be(17);
            query.Limit.Should().Be(23);
        }
    }
}
#endif
