using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues;

/// <summary>
/// Randomised check of the Equals/GetHashCode contract across every BsonType,
/// plus the engine paths that rely on BsonValue hashing (index key dedupe,
/// IN lists, DISTINCT/EXCEPT expressions).
/// </summary>
public class Issue2456_HashContract_Tests
{
    private const int Seed = 2456;
    private const int Iterations = 3000;

    [Fact]
    public void Equal_values_must_share_hash_codes_for_random_inputs()
    {
        var random = new Random(Seed);

        for (var i = 0; i < Iterations; i++)
        {
            var value = RandomValue(random, depth: 0);
            var variant = EquivalentVariant(random, value);
            var other = RandomValue(random, depth: 0);

            AssertContract(value, variant, expectEqual: true);
            AssertContract(value, other, expectEqual: null);
            AssertContract(value, Wrap(value), expectEqual: true);
        }
    }

    [Fact]
    public void Comparison_must_be_antisymmetric_and_never_throw_for_random_inputs()
    {
        var random = new Random(Seed + 1);

        for (var i = 0; i < Iterations; i++)
        {
            // BsonDocument.CompareTo only walks the left-hand keys, so two documents with
            // disjoint keys both compare greater than each other. That ordering quirk predates
            // this change and is outside its scope, so documents are excluded here.
            var left = RandomValue(random, depth: 0, allowDocuments: false);
            var right = RandomValue(random, depth: 0, allowDocuments: false);

            var forward = Math.Sign(left.CompareTo(right));
            var backward = Math.Sign(right.CompareTo(left));

            forward.Should().Be(-backward, $"comparison must be antisymmetric for {left} and {right}");
            Math.Sign(left.CompareTo(left)).Should().Be(0, $"a value must equal itself: {left}");
        }
    }

    [Theory]
    [InlineData(1E+300, 1, 1)]
    [InlineData(1, 1E+300, -1)]
    [InlineData(-1E+300, long.MinValue, -1)]
    [InlineData(double.NaN, 0, -1)]
    [InlineData(0, double.NaN, 1)]
    [InlineData(double.PositiveInfinity, int.MaxValue, 1)]
    [InlineData(double.NegativeInfinity, int.MinValue, -1)]
    [InlineData(7.922816251426434E+28, 1L, 1)]
    public void Doubles_outside_decimal_range_should_order_by_sign_against_other_numbers(object left, object right, int expected)
    {
        var leftValue = new BsonValue(left);
        var rightValue = new BsonValue(right);

        Math.Sign(leftValue.CompareTo(rightValue)).Should().Be(expected);
        Math.Sign(rightValue.CompareTo(leftValue)).Should().Be(-expected);
        leftValue.Equals(rightValue).Should().BeFalse();
        (leftValue == rightValue).Should().BeFalse();
    }

    [Fact]
    public void Double_at_two_pow_96_should_not_equal_decimal_max_value()
    {
        var boundary = new BsonValue((double)decimal.MaxValue);
        var max = new BsonValue(decimal.MaxValue);

        boundary.Equals(max).Should().BeFalse();
        max.Equals(boundary).Should().BeFalse();
        boundary.CompareTo(max).Should().BePositive();
        max.CompareTo(boundary).Should().BeNegative();
    }

    [Fact]
    public void Multikey_index_should_dedupe_numerically_equal_keys()
    {
        using var db = new LiteDatabase(new MemoryStream());
        var col = db.GetCollection("docs");

        col.EnsureIndex("tags", "$.tags[*]");
        col.Insert(new BsonDocument
        {
            ["_id"] = 1,
            ["tags"] = new BsonArray { 1, 1L, 1.0, 1m, "a", "a" }
        });

        col.Find(BsonExpression.Create("$.tags[*] ANY = @0", 1)).Should().HaveCount(1);
        col.Find(BsonExpression.Create("$.tags[*] ANY = @0", 1.0)).Should().HaveCount(1);
        col.Find(BsonExpression.Create("$.tags[*] ANY = @0", "a")).Should().HaveCount(1);
        col.Find(BsonExpression.Create("$.tags[*] ANY = @0", "b")).Should().BeEmpty();
    }

    [Fact]
    public void In_query_should_dedupe_numerically_equal_values()
    {
        using var db = new LiteDatabase(new MemoryStream());
        var col = db.GetCollection("docs");

        col.EnsureIndex("x");
        col.Insert(new BsonDocument { ["_id"] = 1, ["x"] = 1 });
        col.Insert(new BsonDocument { ["_id"] = 2, ["x"] = 2 });

        col.Find(Query.In("x", 1, 1L, 1.0)).Should().HaveCount(1);
        col.Find(Query.In("_id", 1, 1L, 1.0)).Should().HaveCount(1);
        col.Find(Query.In("x", 1, 2L, 2.0)).Should().HaveCount(2);
    }

    [Fact]
    public void Distinct_and_except_expressions_should_use_bson_equality()
    {
        var doc = new BsonDocument
        {
            ["items"] = new BsonArray
            {
                1, 1L, 1.0, 1m, "a", "a",
                new BsonDocument { ["k"] = 1 },
                new BsonDocument { ["K"] = 1L },
                new BsonArray { 1 },
                new BsonArray { 1.0 }
            },
            ["remove"] = new BsonArray { 1L, new BsonDocument { ["k"] = 1.0 } }
        };

        var distinct = BsonExpression.Create("DISTINCT($.items[*])").Execute(doc).ToArray();
        var except = BsonExpression.Create("EXCEPT($.items[*], $.remove[*])").Execute(doc).ToArray();

        distinct.Select(x => x.Type).Should().Equal(BsonType.Int32, BsonType.String, BsonType.Document, BsonType.Array);
        except.Select(x => x.Type).Should().Equal(BsonType.String, BsonType.Array);
    }

    [Fact]
    public void Linq_set_operations_should_use_bson_equality()
    {
        var values = new BsonValue[] { 1, 1L, new BsonValue(new[] { "a" }), new BsonArray { "a" }, new BsonDocument { ["k"] = 1 }, new BsonValue(new Dictionary<string, object> { ["K"] = 1.0 }) };

        values.Distinct().Should().HaveCount(3);
        new HashSet<BsonValue>(values).Should().HaveCount(3);
        values.GroupBy(x => x).Should().HaveCount(3);
        new Dictionary<BsonValue, string> { [values[2]] = "array" }.Should().ContainKey(values[3]);
        new Dictionary<BsonValue, string> { [values[4]] = "document" }.Should().ContainKey(values[5]);
    }

    private static void AssertContract(BsonValue left, BsonValue right, bool? expectEqual)
    {
        var leftEqualsRight = left.Equals(right);
        var rightEqualsLeft = right.Equals(left);

        leftEqualsRight.Should().Be(rightEqualsLeft, $"equality must be symmetric for {left} and {right}");

        if (expectEqual.HasValue)
        {
            leftEqualsRight.Should().Be(expectEqual.Value, $"for {left} and {right}");
        }

        if (leftEqualsRight)
        {
            left.GetHashCode().Should().Be(right.GetHashCode(), $"equal values must share a hash code: {left} and {right}");
        }
    }

    private static BsonValue Wrap(BsonValue value)
    {
        return value.Type switch
        {
            BsonType.Array => new BsonValue((object)value.AsArray),
            BsonType.Document => new BsonValue((object)value.AsDocument),
            _ => new BsonValue((object)value)
        };
    }

    private static BsonValue RandomValue(Random random, int depth, bool allowDocuments = true)
    {
        var maxKind = depth >= 3 ? 14 : allowDocuments ? 16 : 15;

        switch (random.Next(maxKind))
        {
            case 0: return BsonValue.Null;
            case 1: return BsonValue.MinValue;
            case 2: return BsonValue.MaxValue;
            case 3: return random.Next(-5, 6);
            case 4: return (long)random.Next(-5, 6);
            case 5: return RandomDouble(random);
            case 6: return (decimal)random.Next(-5, 6) / (random.Next(2) == 0 ? 1m : 4m);
            case 7: return RandomString(random);
            case 8: return RandomBytes(random);
            case 9: return ObjectId.NewObjectId();
            case 10: return Guid.NewGuid();
            case 11: return random.Next(2) == 0;
            case 12: return new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(random.Next(0, 100000));
            case 13: return new BsonValue(new[] { (float)random.Next(-2, 3), (float)random.Next(-2, 3) });
            case 14: return RandomArray(random, depth + 1, allowDocuments);
            default: return RandomDocument(random, depth + 1);
        }
    }

    private static BsonValue RandomDouble(Random random)
    {
        return random.Next(12) switch
        {
            0 => double.NaN,
            1 => double.PositiveInfinity,
            2 => double.NegativeInfinity,
            3 => 7.922816251426434E+28,
            4 => -7.922816251426434E+28,
            5 => 1E+300,
            6 => -0.0,
            7 => 1E-30,
            _ => random.Next(-5, 6) / (random.Next(2) == 0 ? 1.0 : 4.0)
        };
    }

    private static string RandomString(Random random)
    {
        var chars = new[] { "a", "A", "b", "é", " " };
        return string.Concat(Enumerable.Range(0, random.Next(0, 4)).Select(_ => chars[random.Next(chars.Length)]));
    }

    private static byte[] RandomBytes(Random random)
    {
        var bytes = new byte[random.Next(0, 4)];
        for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)random.Next(3);
        return bytes;
    }

    private static BsonArray RandomArray(Random random, int depth, bool allowDocuments)
    {
        var array = new BsonArray();
        var count = random.Next(0, 4);
        for (var i = 0; i < count; i++) array.Add(RandomValue(random, depth, allowDocuments));
        return array;
    }

    private static BsonDocument RandomDocument(Random random, int depth)
    {
        var document = new BsonDocument();
        var count = random.Next(0, 4);
        var keys = new[] { "a", "b", "c", "_id" };
        for (var i = 0; i < count; i++) document[keys[random.Next(keys.Length)]] = RandomValue(random, depth);
        return document;
    }

    /// <summary>
    /// Build a value that BSON equality must treat as equal to <paramref name="value"/>
    /// while using a different CLR representation where one exists.
    /// </summary>
    private static BsonValue EquivalentVariant(Random random, BsonValue value)
    {
        switch (value.Type)
        {
            case BsonType.Int32:
            case BsonType.Int64:
            case BsonType.Decimal:
                return NumericVariant(random, value.AsDecimal);

            case BsonType.Double:
                // cross-type comparison goes through decimal, which rounds to 28 digits, while
                // double-to-double comparison is exact. Only offer other representations when the
                // decimal round trip is lossless, so the variant is equal under both rules.
                var number = value.AsDouble;
                if (double.IsNaN(number) || double.IsInfinity(number) || Math.Abs(number) >= 7.9e28) return new BsonValue(number);
                if ((double)(decimal)number != number) return new BsonValue(number);
                return NumericVariant(random, (decimal)number);

            case BsonType.Binary:
                return new BsonValue((byte[])value.AsBinary.Clone());

            case BsonType.Vector:
                return new BsonValue((float[])value.AsVector.Clone());

            case BsonType.DateTime:
                return random.Next(2) == 0 ? new BsonValue(value.AsDateTime) : new BsonValue(value.AsDateTime.ToLocalTime());

            case BsonType.String:
                return new BsonValue(new string(value.AsString.ToCharArray()));

            case BsonType.Array:
                return new BsonArray(value.AsArray.Select(item => EquivalentVariant(random, item)));

            case BsonType.Document:
                var document = new BsonDocument();
                var elements = value.AsDocument.ToArray();
                if (random.Next(2) == 0) Array.Reverse(elements);
                foreach (var element in elements)
                {
                    var key = random.Next(2) == 0 ? element.Key : element.Key.ToUpperInvariant();
                    document[key] = EquivalentVariant(random, element.Value);
                }
                return document;

            default:
                return new BsonValue((object)value);
        }
    }

    private static BsonValue NumericVariant(Random random, decimal number)
    {
        var isIntegral = number == Math.Truncate(number);

        switch (random.Next(isIntegral ? 4 : 2))
        {
            case 0: return number;
            case 1: return (double)number;
            case 2: return (int)number;
            default: return (long)number;
        }
    }
}
