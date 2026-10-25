using System;
using System.Linq;
using System.Linq.Expressions;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Mapper
{
    public class LinqResolverRegression_Tests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Nested_lambdas_preserve_volatility_and_immutability(bool filter)
        {
            var mapper = new BsonMapper();
            Expression<Func<Row, object>> lambda = filter
                ? x => x.Dates.Where(d => d < DateTime.UtcNow).ToArray()
                : x => x.Dates.Select(d => DateTime.Now).ToArray();
            using (new DirectTranslationScope()) AssertMetadata(mapper.GetExpression(lambda));
            for (var repeat = 0; repeat < 4; repeat++) AssertMetadata(mapper.GetExpression(lambda));

            void AssertMetadata(BsonExpression expression)
            {
                expression.IsImmutable.Should().BeFalse();
                expression.IsVolatile.Should().BeTrue();
                expression.UseSource.Should().BeFalse();
                expression.Fields.Should().Contain("Dates");
                expression.Bind(new BsonDocument()).IsImmutable.Should().BeFalse();
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Nested_grouping_lambdas_preserve_source_dependency(bool filter)
        {
            var mapper = new BsonMapper();
            Expression<Func<IGrouping<int, Row>, object>> lambda = filter
                ? g => new[] { 1, 2 }.Where(n => g.Count() > n).ToArray()
                : g => new[] { 1, 2 }.Select(n => g.Count()).ToArray();
            using (new DirectTranslationScope()) mapper.GetExpression(lambda).UseSource.Should().BeTrue();
            for (var repeat = 0; repeat < 4; repeat++)
            {
                var expression = mapper.GetExpression(lambda);
                expression.UseSource.Should().BeTrue();
                expression.Bind(new BsonDocument()).UseSource.Should().BeTrue();
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Nested_parameter_bindings_remain_current_and_nonimmutable(bool filter)
        {
            var mapper = new BsonMapper();
            var document = new BsonDocument { ["Values"] = new BsonArray(1, 3, 5) };
            for (var delta = 1; delta <= 4; delta++)
            {
                Expression<Func<Row, object>> lambda = filter
                    ? x => x.Values.Where(n => n > delta).ToArray()
                    : x => x.Values.Select(n => n + delta).ToArray();
                var expression = mapper.GetExpression(lambda);
                expression.IsImmutable.Should().BeFalse();
                expression.IsVolatile.Should().BeFalse();
                var expected = filter ? new[] { 1, 3, 5 }.Where(n => n > delta) : new[] { 1, 3, 5 }.Select(n => n + delta);
                expression.ExecuteScalar(document).AsArray.Select(n => n.AsInt32).Should().Equal(expected);
            }
        }

        [Theory]
        [InlineData(DateTimeKind.Utc)]
        [InlineData(DateTimeKind.Local)]
        [InlineData(DateTimeKind.Unspecified)]
        public void DateTime_conversion_methods_translate_and_execute(DateTimeKind kind)
        {
            var mapper = new BsonMapper();
            var value = new DateTime(2020, 3, 4, 12, 30, 0, kind);
            var document = new BsonDocument { ["Created"] = value };
            foreach (var local in new[] { true, false })
            {
                Expression<Func<Row, DateTime>> lambda = local ? x => x.Created.ToLocalTime() : x => x.Created.ToUniversalTime();
                BsonExpression direct;
                using (new DirectTranslationScope()) direct = mapper.GetExpression(lambda);
                direct.Source.Should().Be(local ? "TO_LOCAL($.Created)" : "TO_UTC($.Created)");
                var expected = local ? value.ToLocalTime() : value.ToUniversalTime();
                var actual = direct.ExecuteScalar(document).AsDateTime;
                actual.Should().Be(expected);
                actual.Kind.Should().Be(expected.Kind);
                var parsed = BsonExpression.Create(direct.Source);
                ExpressionParity.AssertMetadata(direct, parsed);
                direct.ExecuteScalar(document).Should().Be(parsed.ExecuteScalar(document));
            }
            using var db = new LiteDatabase(":memory:", mapper);
            var rows = db.GetCollection<Row>("rows");
            rows.Insert(new Row { Id = 1, Created = value });
            for (var repeat = 0; repeat < 4; repeat++)
            {
                rows.Query().Where(x => x.Created.ToLocalTime().ToUniversalTime() == value.ToUniversalTime())
                    .Select(x => x.Created.ToLocalTime().ToUniversalTime()).ToArray().Should().Equal(value.ToUniversalTime());
            }
        }

        public class Row
        {
            public int Id { get; set; }
            public DateTime Created { get; set; }
            public DateTime[] Dates { get; set; }
            public int[] Values { get; set; }
        }
    }
}
