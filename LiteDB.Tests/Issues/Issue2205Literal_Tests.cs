using System;
using System.Globalization;
using System.Linq;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2205Literal_Tests
    {
        private const string BeyondDecimal = "111111111111111111111111111111111111";

        [Theory]
        [InlineData("9223372036854775808")]
        [InlineData("-9223372036854775809")]
        [InlineData("0009223372036854775808")]
        [InlineData("79228162514264337593543950335")]
        public void Integer_lexemes_beyond_Int64_are_exact_decimals_that_reparse_and_use_indexes(string digits)
        {
            var expected = decimal.Parse(digits, CultureInfo.InvariantCulture);
            var literal = BsonExpression.Create(digits);
            var value = literal.ExecuteScalar();
            Assert.True(value.IsDecimal);
            Assert.Equal(expected, value.AsDecimal);
            Assert.Equal(value, BsonExpression.Create(literal.Source).ExecuteScalar());
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            rows.Insert(new[]
            {
                new BsonDocument { ["_id"] = 1, ["value"] = expected },
                new BsonDocument { ["_id"] = 2, ["value"] = digits }
            });
            rows.EnsureIndex("value");
            Assert.Equal(1, rows.Find("value = " + digits).Single()["_id"].AsInt32);
            Assert.Equal(1, db.Execute("SELECT _id FROM rows WHERE value = " + digits).ToArray().Single()["_id"].AsInt32);
        }

        [Theory]
        [InlineData("79228162514264337593543950336")]
        [InlineData("-" + BeyondDecimal)]
        public void Integer_lexemes_beyond_Decimal_are_doubles_that_reparse(string digits)
        {
            var literal = BsonExpression.Create(digits);
            var value = literal.ExecuteScalar();
            Assert.True(value.IsDouble);
            Assert.Equal(double.Parse(digits, CultureInfo.InvariantCulture), value.AsDouble);
            Assert.Equal(value, BsonExpression.Create(literal.Source).ExecuteScalar());
        }

        [Fact]
        public void Oversized_integer_literals_keep_numeric_arithmetic_and_ordering()
        {
            var sum = BsonExpression.Create("99999999999999999999 + 1").ExecuteScalar();
            Assert.True(sum.IsDecimal);
            Assert.Equal(100000000000000000000m, sum.AsDecimal);
            Assert.True(BsonExpression.Create("99999999999999999999 > 5").ExecuteScalar().AsBoolean);
            Assert.True(BsonExpression.Create(BeyondDecimal + " > 99999999999999999999").ExecuteScalar().AsBoolean);
            Assert.False(BsonExpression.Create("'5' < 99999999999999999999").ExecuteScalar().AsBoolean);
        }

        [Fact]
        public void Integer_lexemes_beyond_Double_are_rejected()
        {
            Assert.Throws<OverflowException>(() => BsonExpression.Create(new string('9', 400)));
        }

        [Fact]
        public void Json_and_sql_document_literals_read_oversized_integers_like_expressions()
        {
            var doc = JsonSerializer.Deserialize("{ a: 99999999999999999999, b: -" + BeyondDecimal + " }").AsDocument;
            Assert.Equal(99999999999999999999m, doc["a"].AsDecimal);
            Assert.True(doc["a"].IsDecimal);
            Assert.Equal(double.Parse("-" + BeyondDecimal, CultureInfo.InvariantCulture), doc["b"].AsDouble);
            Assert.True(doc["b"].IsDouble);
            using var db = new LiteDatabase(":memory:");
            db.Execute("INSERT INTO rows VALUES { _id: 1, value: 99999999999999999999 }");
            Assert.Equal(99999999999999999999m, db.GetCollection("rows").FindById(1)["value"].AsDecimal);
            Assert.Throws<OverflowException>(() => JsonSerializer.Deserialize("{ a: " + new string('9', 400) + " }"));
        }

        [Theory]
        [InlineData("2147483647", BsonType.Int32)]
        [InlineData("2147483648", BsonType.Int64)]
        [InlineData("9223372036854775807", BsonType.Int64)]
        [InlineData("-9223372036854775808", BsonType.Int64)]
        public void Representable_integer_types_and_values_stay_numeric(string text, BsonType type)
        {
            var value = BsonExpression.Create(text).ExecuteScalar();
            Assert.Equal(type, value.Type);
            Assert.Equal(long.Parse(text, System.Globalization.CultureInfo.InvariantCulture), value.AsInt64);
            Assert.Equal(type, JsonSerializer.Deserialize(text).Type);
        }
    }
}
