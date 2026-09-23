using System;
using System.Collections.Generic;
using System.Linq;

using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    /// <summary>
    /// v11 compares mixed numeric types by their exact represented values. Every
    /// query path (index seek, borrowed full scan, owned expression) must agree.
    /// </summary>
    public class ExactNumericScanParity_Tests
    {
        private const long TwoPow53 = 9007199254740992L;

        // Values whose exact ordering differs from decimal-rounded or double-rounded ordering.
        private static readonly BsonValue[] Numbers =
        {
            0, 1, -1, int.MaxValue, int.MinValue,
            0L, 1L, TwoPow53, TwoPow53 + 1, -TwoPow53 - 1, long.MaxValue, long.MinValue,
            0.0, -0.0, 1.0, 0.5, 0.1, -0.1, 19.99, (double)TwoPow53, TwoPow53 + 2.0,
            1e300, -1e300, 1e-300, double.NaN, double.PositiveInfinity, double.NegativeInfinity,
            (double)long.MaxValue, (double)decimal.MaxValue,
            0m, 1m, 1.0m, 0.5m, 0.1m, -0.1m, 19.99m, 9007199254740993m, 0.1000000000000000055511151231m,
            decimal.MaxValue, decimal.MinValue,
        };

        private static readonly string[] Operators = { "=", "!=", "<", "<=", ">", ">=" };

        public static IEnumerable<object[]> Cases()
        {
            // Decimal 0.1 is smaller than binary64 0.1.
            yield return new object[] { new BsonValue(0.1m), "$.x = @0", new BsonValue(0.1) };
            yield return new object[] { new BsonValue(0.1m), "$.x < @0", new BsonValue(0.1) };
            // 2^53 is exactly representable in both Int64 and Double.
            yield return new object[] { new BsonValue(TwoPow53), "$.x = @0", new BsonValue((double)TwoPow53) };
            yield return new object[] { new BsonValue(TwoPow53), "$.x >= @0", new BsonValue((double)TwoPow53) };
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public void Full_scan_filter_matches_index_seek_and_expression(BsonValue stored, string predicate, BsonValue argument)
        {
            using var db = new LiteDatabase(":memory:");
            var plain = db.GetCollection("plain", BsonAutoId.Int32);
            var indexed = db.GetCollection("indexed", BsonAutoId.Int32);
            indexed.EnsureIndex("x");

            var document = new BsonDocument { ["x"] = stored };
            plain.Insert(new BsonDocument(document));
            indexed.Insert(new BsonDocument(document));

            var owned = BsonExpression.Create(predicate, argument).ExecuteScalar(document).AsBoolean ? 1 : 0;

            indexed.Count(BsonExpression.Create(predicate, argument)).Should().Be(owned, "index seek of {0} with {1}", predicate, argument);
            plain.Count(BsonExpression.Create(predicate, argument)).Should().Be(owned, "full scan of {0} with {1}", predicate, argument);
        }

        [Fact]
        public void DeleteMany_without_index_deletes_only_exactly_equal_numbers()
        {
            using var db = new LiteDatabase(":memory:");
            var plain = db.GetCollection("plain", BsonAutoId.Int32);
            var indexed = db.GetCollection("indexed", BsonAutoId.Int32);
            indexed.EnsureIndex("price");

            foreach (var collection in new[] { plain, indexed })
                collection.Insert(new BsonDocument { ["price"] = 0.1m });

            // Decimal 0.1 is not equal to binary64 0.1 under v11 ordering.
            BsonExpression.Create("$.price = @0", 0.1).ExecuteScalar(new BsonDocument { ["price"] = 0.1m })
                .AsBoolean.Should().BeFalse();

            indexed.DeleteMany(BsonExpression.Create("$.price = @0", 0.1)).Should().Be(0, "indexed delete");
            plain.DeleteMany(BsonExpression.Create("$.price = @0", 0.1)).Should().Be(0, "full-scan delete");
            plain.UpdateMany(BsonExpression.Create("{ price: 1 }"), BsonExpression.Create("$.price = @0", 0.1)).Should().Be(0);
            plain.Count("$.price = DECIMAL(0.1)").Should().Be(1, "the documented workaround for double literals");
            indexed.Count("$.price = DECIMAL(0.1)").Should().Be(1);
            plain.DeleteMany(BsonExpression.Create("$.price = @0", 0.1m)).Should().Be(1, "the decimal parameter matches exactly");
            indexed.DeleteMany(BsonExpression.Create("$.price = @0", 0.1m)).Should().Be(1);
        }

        [Theory]
        [InlineData(false, null)]
        [InlineData(true, null)]
        [InlineData(true, "secret")]
        public void Scan_seek_and_in_memory_comparison_select_the_same_mixed_numbers(bool onDisk, string password)
        {
            using var file = new TempFile();
            var connection = new ConnectionString { Filename = onDisk ? file.Filename : ":memory:", Password = password };
            using var db = new LiteDatabase(connection);
            var plain = db.GetCollection("plain", BsonAutoId.Int32);
            var indexed = db.GetCollection("indexed", BsonAutoId.Int32);
            indexed.EnsureIndex("x");

            for (var i = 0; i < Numbers.Length; i++)
            {
                plain.Insert(new BsonDocument { ["_id"] = i, ["x"] = Numbers[i] });
                indexed.Insert(new BsonDocument { ["_id"] = i, ["x"] = Numbers[i] });
            }

            foreach (var argument in Numbers)
            {
                foreach (var op in Operators)
                {
                    var expected = Expected(v => Apply(op, v.CompareTo(argument)));
                    AssertSelects(plain, indexed, $"$.x {op} @0", expected, argument);
                }
            }

            var random = new Random(2026092311);
            for (var round = 0; round < 200; round++)
            {
                var a = Numbers[random.Next(Numbers.Length)];
                var b = Numbers[random.Next(Numbers.Length)];
                var c = Numbers[random.Next(Numbers.Length)];

                AssertSelects(plain, indexed, "$.x BETWEEN @0 AND @1",
                    Expected(v => v.CompareTo(a) >= 0 && v.CompareTo(b) <= 0), a, b);
                AssertSelects(plain, indexed, "$.x IN @0",
                    Expected(v => v.CompareTo(a) == 0 || v.CompareTo(b) == 0 || v.CompareTo(c) == 0), new BsonArray { a, b, c });
            }
        }

        [Fact]
        public void Group_by_merges_exactly_equal_numbers_only()
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows", BsonAutoId.Int32);
            foreach (var value in new BsonValue[] { 1, 1L, 1.0, 1m, 1.00m, 0.1, 0.1m, 0.10m, TwoPow53, (double)TwoPow53, TwoPow53 + 1 })
                rows.Insert(new BsonDocument { ["x"] = value });

            var groups = db.Execute("SELECT { k: @key, n: COUNT(*) } FROM rows GROUP BY $.x").ToList()
                .Select(x => x["n"].AsInt32).OrderBy(x => x).ToArray();

            // {1,1L,1.0,1m,1.00m}, {0.1}, {0.1m,0.10m}, {2^53 long, 2^53 double}, {2^53+1}
            groups.Should().Equal(1, 1, 2, 2, 5);
        }

        private static HashSet<int> Expected(Func<BsonValue, bool> predicate) =>
            new HashSet<int>(Enumerable.Range(0, Numbers.Length).Where(i => predicate(Numbers[i])));

        private static bool Apply(string op, int comparison)
        {
            switch (op)
            {
                case "=": return comparison == 0;
                case "!=": return comparison != 0;
                case "<": return comparison < 0;
                case "<=": return comparison <= 0;
                case ">": return comparison > 0;
                default: return comparison >= 0;
            }
        }

        private static void AssertSelects(ILiteCollection<BsonDocument> plain, ILiteCollection<BsonDocument> indexed,
            string predicate, HashSet<int> expected, params BsonValue[] arguments)
        {
            var because = $"{predicate} with {string.Join(", ", arguments.Select(x => x.ToString()))}";

            foreach (var i in Enumerable.Range(0, Numbers.Length))
            {
                var owned = BsonExpression.Create(predicate, arguments).ExecuteScalar(new BsonDocument { ["x"] = Numbers[i] }).AsBoolean;
                owned.Should().Be(expected.Contains(i), "in-memory evaluation of {0} for {1}", because, Numbers[i]);
            }

            Ids(plain.Find(BsonExpression.Create(predicate, arguments))).Should().BeEquivalentTo(expected, "full scan of {0}", because);
            Ids(indexed.Find(BsonExpression.Create(predicate, arguments))).Should().BeEquivalentTo(expected, "index seek of {0}", because);
        }

        private static IEnumerable<int> Ids(IEnumerable<BsonDocument> documents) => documents.Select(x => x["_id"].AsInt32);
    }
}
