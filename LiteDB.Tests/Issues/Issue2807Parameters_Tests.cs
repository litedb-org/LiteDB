using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2807Parameters_Tests
    {
        [Fact]
        public void Original_helpers_keep_self_contained_string_and_SQL_contracts()
        {
            var literal = Query.EQ("value", 7);
            literal.Parameters.Count.Should().Be(0);
            BsonExpression reparsed = (string)literal;
            reparsed.ExecuteScalar(new BsonDocument { ["value"] = 7 }).AsBoolean.Should().BeTrue();
            using var db = new LiteDatabase(":memory:");
            db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = 7 });
            var query = new Query();
            query.Where.Add(literal);
            db.Execute(query.ToSQL("rows")).ToArray().Select(d => d["_id"].AsInt32).Should().Equal(1);
            var any = Query.Any().EQ("values[*]", 7);
            any.Parameters.Count.Should().Be(0);
            ((BsonExpression)(string)any).ExecuteScalar(new BsonDocument { ["values"] = new BsonArray(7) }).AsBoolean.Should().BeTrue();
        }

        [Fact]
        public void Mutable_arguments_remain_snapshots_and_composed_queries_keep_independent_bindings()
        {
            var nested = new BsonDocument { ["label"] = "before" };
            var values = new BsonArray(1, nested);
            var first = Query.Parameterized.In("value", values);
            nested["label"] = "after";
            values.Add(2);
            first.ExecuteScalar(new BsonDocument { ["value"] = 2 }).AsBoolean.Should().BeFalse();
            first.ExecuteScalar(new BsonDocument { ["value"] = new BsonDocument { ["label"] = "before" } }).AsBoolean.Should().BeTrue();
            var composed = Query.Or(Query.Parameterized.EQ("value", "a"), Query.Parameterized.EQ("value", "b"));
            foreach (var value in new[] { "a", "b", "c" })
                composed.ExecuteScalar(new BsonDocument { ["value"] = value }).AsBoolean.Should().Be(value != "c");
        }

        [Fact]
        public void Composition_snapshots_mutable_operand_parameter_values()
        {
            var leaf = Query.Parameterized.In("value", new BsonArray(1, new BsonDocument { ["label"] = "before" }));
            var composed = Query.Or(leaf, Query.Parameterized.EQ("value", 2));
            var supplied = leaf.Parameters["__queryValue0"].AsArray;
            supplied[0] = 3;
            supplied[1].AsDocument["label"] = "after";
            foreach (var value in new[] { 1, 2, 3 })
                composed.ExecuteScalar(new BsonDocument { ["value"] = value }).AsBoolean.Should().Be(value != 3);
            composed.ExecuteScalar(new BsonDocument { ["value"] = new BsonDocument { ["label"] = "before" } })
                .AsBoolean.Should().BeTrue();
        }

        [Fact]
        public void Field_parameters_stay_unbound_even_when_their_name_matches_the_helper_prefix()
        {
            var expression = Query.Parameterized.EQ("@__queryValue0", 42);
            expression.ExecuteScalar(new BsonDocument()).AsBoolean.Should().BeFalse();
            expression.Parameters.ContainsKey("__queryValue0").Should().BeFalse();
        }

        [Fact]
        public void Any_helpers_reuse_source_and_keep_parameter_values()
        {
            var helpers = Query.Parameterized.Any();
            var first = helpers.EQ("values[*]", "a");
            var second = helpers.EQ("values[*]", "b");
            first.Source.Should().Be(second.Source);
            foreach (var value in new[] { "a", "b", "c" })
            {
                var row = new BsonDocument { ["values"] = new BsonArray(value) };
                first.ExecuteScalar(row).AsBoolean.Should().Be(value == "a");
                second.ExecuteScalar(row).AsBoolean.Should().Be(value == "b");
            }
        }

        [Fact]
        public void Repeated_values_share_compiled_delegates_with_independent_results()
        {
            var expressions = Enumerable.Range(0, 50).Select(i => Query.Parameterized.EQ("value", i)).ToArray();
            expressions.Select(e => e.Source).Distinct().Should().ContainSingle();
            for (var i = 0; i < expressions.Length; i++)
                expressions[i].ExecuteScalar(new BsonDocument { ["value"] = i }).AsBoolean.Should().BeTrue();
        }
    }
}
