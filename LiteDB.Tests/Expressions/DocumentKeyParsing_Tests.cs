using System;
using System.Text;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Expressions
{
    public class DocumentKeyParsing_Tests
    {
        [Theory]
        [InlineData("word", "word", "word")]
        [InlineData("'with space'", "with space", "\"with space\"")]
        [InlineData("'a.b'", "a.b", "\"a.b\"")]
        [InlineData("123", "123", "\"123\"")]
        [InlineData("''", "", "\"\"")]
        [InlineData("\"a\\\"b\"", "a\"b", "\"a\\\"b\"")]
        [InlineData("'雪'", "雪", "雪")]
        public void ReadKey_overloads_consume_the_same_token_and_preserve_formatting(string input, string key, string formatted)
        {
            var plain = new Tokenizer(input + ": 1");
            BsonExpressionParser.ReadKey(plain).Should().Be(key);
            plain.ReadToken().Type.Should().Be(TokenType.Colon);
            var withSource = new Tokenizer(input + ": 1");
            var source = new StringBuilder("prefix:");
            BsonExpressionParser.ReadKey(withSource, source).Should().Be(key);
            source.ToString().Should().Be("prefix:" + formatted);
            withSource.ReadToken().Type.Should().Be(TokenType.Colon);
            var document = BsonExpression.Create("{" + input + ": 1}");
            document.Source.Should().Be("{" + formatted + ":1}");
            // Empty keys are accepted by the reader but not by document execution.
            if (key.Length > 0) document.ExecuteScalar().AsDocument[key].AsInt32.Should().Be(1);
        }

        [Theory]
        [InlineData(":")]
        [InlineData("[")]
        [InlineData("1.5")]
        [InlineData("")]
        public void Invalid_key_tokens_keep_their_errors(string input)
        {
            Action plain = () => BsonExpressionParser.ReadKey(new Tokenizer(input));
            Action formatted = () => BsonExpressionParser.ReadKey(new Tokenizer(input), new StringBuilder());
            var expected = formatted.Should().Throw<LiteException>().Which;
            var actual = plain.Should().Throw<LiteException>().Which;
            actual.ErrorCode.Should().Be(expected.ErrorCode);
            actual.Message.Should().Be(expected.Message);
        }

        [Fact]
        public void Sql_update_and_document_projection_keep_escaped_key_values()
        {
            using var db = new LiteDatabase(":memory:");
            db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
            using (var update = db.Execute("UPDATE rows SET 'a.b' = @value, 123 = @value + 1, 'a b' = @value + 2 WHERE _id = 1",
                new BsonDocument { ["value"] = 7 }))
            {
                update.Read().Should().BeTrue();
                update.Current.AsInt32.Should().Be(1);
            }
            using var result = db.Execute("SELECT { 'a.b': $.[\"a.b\"], 123: $.[\"123\"], 'a b': $.[\"a b\"] } FROM rows WHERE _id = 1");
            result.Read().Should().BeTrue();
            result.Current.Should().Be(new BsonDocument { ["a.b"] = 7, ["123"] = 8, ["a b"] = 9 });
        }
    }
}
