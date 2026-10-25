using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2800_Tests
    {
        [Fact]
        public void Cached_sort_keys_and_array_indexes_use_current_parameters()
        {
            var root = new BsonDocument { ["Items"] = new BsonArray(11, 22, 33) };
            foreach (var direction in new[] { 1, -1, 1 })
            {
                var expression = BsonExpression.Create("SORT($.Items[*] => @ * @0)", direction);
                expression.Execute(root).Select(x => x.AsInt32).Should()
                    .Equal(direction == 1 ? new[] { 11, 22, 33 } : new[] { 33, 22, 11 });
            }
            foreach (var index in new[] { 0, 2, 1, 0 })
            {
                BsonExpression.Create("$.Items[@0]", index).ExecuteScalar(root).AsInt32
                    .Should().Be(new[] { 11, 22, 33 }[index]);
            }
        }

        [Fact]
        public void Nested_cached_enumerators_keep_parameters_when_interleaved()
        {
            const string source = "MAP($.Items[*] => MAP(@[*] => @ + @0))";
            var root = new BsonDocument
            {
                ["Items"] = new BsonArray(new BsonArray(1, 2), new BsonArray(3, 4))
            };
            var first = BsonExpression.Create(source, 10);
            var second = BsonExpression.Create(source, 100);
            using var left = first.Execute(root).GetEnumerator();
            using var right = second.Execute(root).GetEnumerator();
            foreach (var value in new[] { 1, 2, 3, 4 })
            {
                left.MoveNext().Should().BeTrue();
                left.Current.AsInt32.Should().Be(value + 10);
                right.MoveNext().Should().BeTrue();
                right.Current.AsInt32.Should().Be(value + 100);
            }
            left.MoveNext().Should().BeFalse();
            right.MoveNext().Should().BeFalse();
            first.Parameters["0"].AsInt32.Should().Be(10);
            second.Parameters["0"].AsInt32.Should().Be(100);
        }

        [Fact]
        public void Concurrent_cached_filters_do_not_share_parameter_state()
        {
            const string source = "FILTER($.ConcurrentItems[*] => @ = @0)";
            Parallel.For(0, 100, value =>
            {
                var root = new BsonDocument { ["ConcurrentItems"] = new BsonArray(value, value + 1) };
                var expression = BsonExpression.Create(source, value);
                expression.Execute(root).Select(x => x.AsInt32).Should().Equal(value);
                expression.Parameters["0"].AsInt32.Should().Be(value);
            });
        }

        [Theory]
        [InlineData("COUNT(FILTER($.Items[*] => @ = @0)) > 0")]
        [InlineData("COUNT($.Items[@ = @0]) > 0")]
        [InlineData("MAP($.Items[*] => @ = @0) ANY = true")]
        public void Nested_expression_parameters_are_fresh_across_queries_and_databases(string source)
        {
            using var file = new TempFile();
            using (var db = new LiteDatabase(file.Filename))
            {
                var col = db.GetCollection("items");
                col.Insert(new BsonDocument { ["_id"] = 1, ["Items"] = new BsonArray(11, 13) });
                col.Insert(new BsonDocument { ["_id"] = 2, ["Items"] = new BsonArray(22) });
                col.Insert(new BsonDocument { ["_id"] = 3, ["Items"] = new BsonArray() });
            }
            foreach (var value in new[] { 11, 22, 99, 22, 11 })
            {
                using var db = new LiteDatabase(file.Filename);
                var col = db.GetCollection("items");
                var expected = col.FindAll().Where(x => x["Items"].AsArray.Any(v => v.AsInt32 == value))
                    .Select(x => x["_id"].AsInt32).OrderBy(x => x).ToArray();
                var expression = BsonExpression.Create(source, new BsonValue(value));
                col.Find(expression).Select(x => x["_id"].AsInt32).OrderBy(x => x).Should().Equal(expected);
                expression.Parameters["0"].AsInt32.Should().Be(value);
                col.Count().Should().Be(3);
            }
        }
    }
}
