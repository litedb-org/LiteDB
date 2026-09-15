using System;
using System.Linq;

using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Audit2026
{
    public class JsonAuditRegression_Tests
    {
        [Fact]
        [Trait("Category", "AuditBehavior")]
        public void M125_empty_document_key_round_trips()
        {
            var document = JsonSerializer.Deserialize("{\"\":1}").AsDocument;
            document.ContainsKey("").Should().BeTrue();
            document[""].AsInt32.Should().Be(1);
        }

        [Fact]
        [Trait("Category", "AuditBehavior")]
        public void M127_document_keys_are_json_escaped()
        {
            var original = new BsonDocument { ["a\"},\"injected\":true,\"x"] = 1 };
            var json = JsonSerializer.Serialize(original);
            var result = JsonSerializer.Deserialize(json).AsDocument;

            result.Count.Should().Be(1);
            result.Keys.ToArray().Should().ContainSingle().Which.Should().Be("a\"},\"injected\":true,\"x");
        }

        [Theory]
        [InlineData(0.0000000001d)]
        [InlineData(1.2345678901234567d)]
        [Trait("Category", "AuditBehavior")]
        public void M128_double_round_trip_preserves_value(double expected)
        {
            var json = JsonSerializer.Serialize(new BsonValue(expected));
            JsonSerializer.Deserialize(json).AsDouble.Should().Be(expected);
        }

        [Fact]
        [Trait("Category", "AuditBehavior")]
        public void M129_large_integer_is_reported_as_litedb_parse_error()
        {
            Action deserialize = () => JsonSerializer.Deserialize("9223372036854775808");
            deserialize.Should().Throw<LiteException>();
        }

        [Theory]
        [InlineData("{\"a\":1} {\"b\":2}")]
        [InlineData("[1,2,3]]")]
        [InlineData("true false")]
        [Trait("Category", "AuditBehavior")]
        public void L191_trailing_json_content_is_rejected(string json)
        {
            Action deserialize = () => JsonSerializer.Deserialize(json);
            deserialize.Should().Throw<LiteException>();
        }
    }
}
