using System;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue1715_Tests
    {
        public class Row
        {
            public int Id { get; set; }
            public int A { get; set; }
            public int B { get; set; }
        }

        [Fact]
        public void Composed_predicates_keep_independent_parameters_and_truth_tables()
        {
            using var db = new LiteDatabase(":memory:");
            var col = db.GetCollection<Row>();
            var rows = new[]
            {
                new Row { Id = 1, A = 1, B = 2 }, new Row { Id = 2, A = 2, B = 1 },
                new Row { Id = 3, A = 1, B = 1 }, new Row { Id = 4, A = 2, B = 2 }
            };
            col.Insert(rows);
            var mapper = new BsonMapper();
            var left = mapper.GetExpression<Row, bool>(x => x.A == 1);
            var right = mapper.GetExpression<Row, bool>(x => x.B == 2);
            col.Find(left).Select(x => x.Id).OrderBy(x => x).Should().Equal(1, 3);
            col.Find(right).Select(x => x.Id).OrderBy(x => x).Should().Equal(1, 4);
            col.Find(Query.And(left, right)).Select(x => x.Id).Should().Equal(1);
            col.Find(Query.And(right, left)).Select(x => x.Id).Should().Equal(1);
            col.Find(Query.Or(left, right)).Select(x => x.Id).OrderBy(x => x).Should().Equal(1, 3, 4);
            // Composition must not change either input expression's meaning.
            col.Find(left).Select(x => x.Id).OrderBy(x => x).Should().Equal(1, 3);
            col.Find(right).Select(x => x.Id).OrderBy(x => x).Should().Equal(1, 4);
        }

        [Fact]
        public void Nested_and_variadic_composition_does_not_mutate_or_cross_bind_inputs()
        {
            var first = BsonExpression.Create("@p0=1 AND @P0=1", new BsonDocument { ["p0"] = 1 });
            var second = BsonExpression.Create("@p0=2", new BsonDocument { ["p0"] = 2 });
            var third = BsonExpression.Create("@0=3", 3);
            var sources = new[] { first.Source, second.Source, third.Source };
            var all = Query.And(first, second, third);
            all.ExecuteScalar().AsBoolean.Should().BeTrue();
            Query.Or(BsonExpression.Create("false"), all, third).ExecuteScalar().AsBoolean.Should().BeTrue();
            Query.And(all, Query.Or(first, second)).ExecuteScalar().AsBoolean.Should().BeTrue();
            new[] { first.Source, second.Source, third.Source }.Should().Equal(sources);
            first.Parameters.Keys.Should().Equal("p0");
            second.Parameters["p0"].AsInt32.Should().Be(2);
            third.Parameters["0"].AsInt32.Should().Be(3);

            first.Parameters["p0"] = 8;
            first.ExecuteScalar().AsBoolean.Should().BeFalse();
            all.ExecuteScalar().AsBoolean.Should().BeTrue("composition snapshots parameter bindings without mutating inputs");
            Query.And(first, second).ExecuteScalar().AsBoolean.Should().BeFalse();
        }

        [Fact]
        public void Quoted_names_strings_current_item_paths_and_nested_parameters_are_preserved()
        {
            var source = new BsonDocument
            {
                ["@p0"] = "@p0 'quoted' \\ value", ["items"] = new BsonArray { 1, 2, 3 }
            };
            var text = JsonSerializer.Serialize(source["@p0"]);
            var left = BsonExpression.Create("$.[\"@p0\"]=" + text + " AND @p0=1", new BsonDocument { ["p0"] = 1 });
            var right = BsonExpression.Create("COUNT(FILTER($.items => @ >= @p0))=@count",
                new BsonDocument { ["p0"] = 2, ["count"] = 2 });
            Query.And(left, right).ExecuteScalar(source).AsBoolean.Should().BeTrue();
            Query.And(right, left).ExecuteScalar(source).AsBoolean.Should().BeTrue();
        }

        [Fact]
        public void Unbound_parameters_remain_unbound_and_ordinary_key_parameters_stay_independent()
        {
            var unbound = BsonExpression.Create("@q0=null");
            var bound = BsonExpression.Create("@q0=1", new BsonDocument { ["q0"] = 1 });
            Query.And(unbound, bound).ExecuteScalar().AsBoolean.Should().BeTrue();
            Query.And(bound, unbound).ExecuteScalar().AsBoolean.Should().BeTrue();
            var left = BsonExpression.Create("@key=1", new BsonDocument { ["key"] = 1 });
            var right = BsonExpression.Create("@KEY=2", new BsonDocument { ["KEY"] = 2 });
            Query.And(left, right).ExecuteScalar().AsBoolean.Should().BeTrue();
        }

        [Fact]
        public void Composed_having_predicates_rebind_group_keys_even_after_prior_execution()
        {
            using var db = new LiteDatabase(":memory:");
            var col = db.GetCollection<Row>();
            col.Insert(new[] { new Row { Id = 1, A = 1 }, new Row { Id = 2, A = 1 }, new Row { Id = 3, A = 2 } });
            var key = BsonExpression.Create("@key=@p0", new BsonDocument { ["p0"] = 1 });
            var count = BsonExpression.Create("COUNT(*)>=@p0", new BsonDocument { ["p0"] = 2 });
            var having = Query.And(key, count);
            for (var i = 0; i < 2; i++)
            {
                var result = col.Query().GroupBy(x => x.A).Having(having)
                    .Select(g => new { g.Key, Count = g.Count() }).ToArray();
                result.Should().ContainSingle();
                result[0].Key.Should().Be(1);
                result[0].Count.Should().Be(2);
                having = Query.And(having, BsonExpression.Create("@key>0"));
            }
            key.Parameters.ContainsKey("key").Should().BeFalse();
            count.Parameters["p0"].AsInt32.Should().Be(2);
        }
    }
}
