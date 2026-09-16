using System;
using System.Linq;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2205Literal_Tests
    {
        [Theory]
        [InlineData("9223372036854775808")]
        [InlineData("-9223372036854775809")]
        [InlineData("0009223372036854775808")]
        public void Overflowing_integer_lexemes_preserve_complete_spelling_and_reparse_as_strings(string digits)
        {
            var literal = BsonExpression.Create(digits);
            var value = literal.ExecuteScalar();
            Assert.True(value.IsString);
            Assert.Equal(digits, value.AsString);
            Assert.Equal(digits, BsonExpression.Create(literal.Source).ExecuteScalar().AsString);
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            rows.Insert(new[]
            {
                new BsonDocument { ["_id"] = 1, ["value"] = digits },
                new BsonDocument { ["_id"] = 2, ["value"] = digits + "1" }
            });
            rows.EnsureIndex("value");
            Assert.Equal(1, rows.Find("value = " + digits).Single()["_id"].AsInt32);
            Assert.Equal(1, db.Execute("SELECT _id FROM rows WHERE value = " + digits).ToArray().Single()["_id"].AsInt32);
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
        }
    }
}
