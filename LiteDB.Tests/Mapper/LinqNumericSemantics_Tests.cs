using System;
using System.Linq;
using System.Linq.Expressions;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Mapper
{
    public class LinqNumericSemantics_Tests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Floating_negation_preserves_signed_zero_and_double_negation(bool uncached)
        {
            using var scope = uncached ? new DirectTranslationScope() : null;
            var mapper = new BsonMapper();
            var values = new[] { 0d, BitConverter.Int64BitsToDouble(long.MinValue), 1d, -1d,
                double.PositiveInfinity, double.NegativeInfinity, double.NaN };
            Expression<Func<NumericRow, double>>[] selectors =
            {
                x => -x.Double, x => -(-x.Double), x => -x.Float, x => -(-x.Float),
            };
            foreach (var selector in selectors)
            {
                var compiled = selector.Compile();
                for (var repeat = 0; repeat < 3; repeat++)
                {
                    var translated = mapper.GetExpression(selector);
                    foreach (var value in values)
                    {
                        var row = new NumericRow { Double = value, Float = (float)value };
                        var expected = compiled(row);
                        var actual = translated.ExecuteScalar(mapper.ToDocument(row)).AsDouble;
                        if (double.IsNaN(expected)) double.IsNaN(actual).Should().BeTrue();
                        else BitConverter.DoubleToInt64Bits(actual).Should().Be(BitConverter.DoubleToInt64Bits(expected));
                    }
                }
            }
        }

        [Fact]
        public void Signed_zero_queries_match_CLR_after_BSON_roundtrip()
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection<NumericRow>();
            var negativeZero = BitConverter.Int64BitsToDouble(long.MinValue);
            rows.Insert(new[] { new NumericRow { Id = 1, Double = 0d, Float = 0f },
                new NumericRow { Id = 2, Double = negativeZero, Float = (float)negativeZero },
                new NumericRow { Id = 3, Double = 1d, Float = 1f } });
            var stored = rows.FindAll().ToArray();
            foreach (var predicate in new Expression<Func<NumericRow, bool>>[]
            {
                x => 1d / -x.Double > 0d, x => 1d / -(-x.Double) < 0d,
                x => 1f / -x.Float > 0f, x => 1f / -(-x.Float) < 0f
            })
                rows.Find(predicate).Select(x => x.Id).Should().BeEquivalentTo(stored.Where(predicate.Compile()).Select(x => x.Id));
        }

        [Fact]
        public void Integral_decimal_and_nullable_negation_keep_existing_semantics()
        {
            var mapper = new BsonMapper();
            var rows = new[] { new NumericRow { Int = int.MinValue, Long = long.MinValue, Decimal = decimal.MinValue },
                new NumericRow { Int = 0, Long = 0, Decimal = 0m, NullableDouble = 0d, NullableFloat = 0f },
                new NumericRow { Int = 5, Long = 5, Decimal = 1.25m, NullableDouble = -2d, NullableFloat = -2f } };
            foreach (var row in rows)
            {
                var doc = mapper.ToDocument(row);
                mapper.GetExpression<NumericRow, int>(x => -x.Int).ExecuteScalar(doc).AsInt32.Should().Be(unchecked(-row.Int));
                mapper.GetExpression<NumericRow, long>(x => -x.Long).ExecuteScalar(doc).AsInt64.Should().Be(unchecked(-row.Long));
                mapper.GetExpression<NumericRow, decimal>(x => -x.Decimal).ExecuteScalar(doc).AsDecimal.Should().Be(-row.Decimal);
                var actual = mapper.GetExpression<NumericRow, double?>(x => -x.NullableDouble).ExecuteScalar(doc);
                if (row.NullableDouble == null) actual.IsNull.Should().BeTrue();
                else BitConverter.DoubleToInt64Bits(actual.AsDouble).Should().Be(BitConverter.DoubleToInt64Bits(-row.NullableDouble.Value));
                var single = mapper.GetExpression<NumericRow, float?>(x => -x.NullableFloat).ExecuteScalar(doc);
                if (row.NullableFloat == null) single.IsNull.Should().BeTrue();
                else BitConverter.DoubleToInt64Bits(single.AsDouble).Should().Be(BitConverter.DoubleToInt64Bits(-row.NullableFloat.Value));
            }
        }

        [Fact]
        public void Floating_negation_source_roundtrips_with_signed_zero()
        {
            var mapper = new BsonMapper();
            var translated = mapper.GetExpression<NumericRow, double>(x => -(-x.Double));
            var parsed = BsonExpression.Create(translated.Source, translated.Parameters);
            var doc = mapper.ToDocument(new NumericRow { Double = BitConverter.Int64BitsToDouble(long.MinValue) });
            BitConverter.DoubleToInt64Bits(parsed.ExecuteScalar(doc).AsDouble).Should().Be(long.MinValue);
        }

        // BSON comparisons intentionally use a total order: NaN equals NaN and sorts below numbers.
        // Negation preserves NaN; this fix does not replace BSON comparison with CLR IEEE operators.
        [Fact]
        public void NaN_remains_NaN_and_keeps_BSON_total_order_comparisons()
        {
            var mapper = new BsonMapper();
            var doc = mapper.ToDocument(new NumericRow { Double = double.NaN });
            double.IsNaN(mapper.GetExpression<NumericRow, double>(x => -x.Double).ExecuteScalar(doc).AsDouble).Should().BeTrue();
            mapper.GetExpression<NumericRow, bool>(x => -x.Double == double.NaN).ExecuteScalar(doc).AsBoolean.Should().BeTrue();
            mapper.GetExpression<NumericRow, bool>(x => -x.Double < 0d).ExecuteScalar(doc).AsBoolean.Should().BeTrue();
        }

        public class NumericRow
        {
            public int Id { get; set; }
            public double Double { get; set; }
            public float Float { get; set; }
            public int Int { get; set; }
            public long Long { get; set; }
            public decimal Decimal { get; set; }
            public double? NullableDouble { get; set; }
            public float? NullableFloat { get; set; }
        }
    }
}
