using System.Linq;
using FluentAssertions;
using LiteDB.Tests.Mapper;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public class NestedExpressionSource_Tests
    {
        [Theory]
        [InlineData("ARRAY(MAP(Values => @ + $.Offset + @delta))", 14, 11, 13)]
        [InlineData("ARRAY(MAP(Values => @ + COUNT(*) + @delta))", 5, 2, 4)]
        [InlineData("ARRAY(FILTER(Values => @ >= @minimum))", 3, 2)]
        [InlineData("ARRAY(FILTER(Values => @ >= COUNT(*) + @delta))", 3, 2)]
        [InlineData("ARRAY(Values[@ >= @minimum])", 3, 2)]
        [InlineData("ARRAY(Values[@ >= COUNT(*) + @delta])", 3, 2)]
        [InlineData("ARRAY(SORT(Values => @ * @direction))", 3, 2, 0)]
        [InlineData("ARRAY(SORT(Values => @ * (COUNT(*) - 2)))", 3, 2, 0)]
        [InlineData("ARRAY(MAP(Values => FIRST(*.Offset) + @))", 13, 10, 12)]
        public void Nested_expressions_keep_root_current_source_and_parameters(string source, params int[] expected)
        {
            BsonExpression template;
            using (var scope = new DirectTranslationScope())
            {
                Tokenizer.ForbidCreation = false;
                template = BsonExpression.Create(source, new BsonDocument
                {
                    ["delta"] = 1, ["minimum"] = 2, ["direction"] = -1
                });
            }
            var document = new BsonDocument { ["Offset"] = 10, ["Values"] = new BsonArray(3, 0, 2) };
            var values = template.Execute(document);
            values.Single().AsArray.Select(x => x.AsInt32).Should().Equal(expected);
            values.Single().AsArray.Select(x => x.AsInt32).Should().Equal(expected);
            document["Values"] = new BsonArray();
            template.ExecuteScalar(document).AsArray.Count.Should().Be(0);
        }

        [Fact]
        public void Nested_sources_and_parameterized_indexes_use_each_bindings_parameters()
        {
            var template = BsonExpression.Create("{ values: ARRAY(MAP(Values => @ + COUNT(*) + @delta)), picked: Values[@index] }",
                new BsonDocument { ["delta"] = 1, ["index"] = 0 });
            var document = new BsonDocument { ["Values"] = new BsonArray(3, 0, 2) };
            var rebound = template.Bind(new BsonDocument { ["delta"] = 5, ["index"] = -1 });
            template.ExecuteScalar(document)["values"].AsArray.Select(x => x.AsInt32).Should().Equal(5, 2, 4);
            rebound.ExecuteScalar(document)["values"].AsArray.Select(x => x.AsInt32).Should().Equal(9, 6, 8);
            template.ExecuteScalar(document)["picked"].AsInt32.Should().Be(3);
            rebound.ExecuteScalar(document)["picked"].AsInt32.Should().Be(2);
        }

        [Fact]
        public void Nested_sort_and_filter_keep_the_active_collation()
        {
            var collation = new Collation("en-US/IgnoreCase");
            var document = new BsonDocument { ["Values"] = new BsonArray("a", "B", "A") };
            BsonExpression.Create("ARRAY(SORT(Values => @))").ExecuteScalar(document, collation)
                .AsArray.Select(x => x.AsString).Should().Equal("a", "A", "B");
            BsonExpression.Create("ARRAY(FILTER(Values => @ = 'A'))").ExecuteScalar(document, collation)
                .AsArray.Select(x => x.AsString).Should().Equal("a", "A");
        }
    }
}
