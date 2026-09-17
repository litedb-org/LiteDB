using System;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2746_Tests
    {
        [Theory]
        [InlineData("2e4e2069_da7c_4dbd_8c11_a5ac31041079", false)]
        [InlineData("a2e4e2069_da7c_4dbd_8c11_a5ac31041079", true)]
        [InlineData("_2", true)]
        public void Collection_name_contract_distinguishes_initial_digits_from_later_digits(string name, bool valid)
        {
            using var db = new LiteDatabase(":memory:");
            Action insert = () => db.GetCollection(name).Insert(new BsonDocument { ["_id"] = 1, ["value"] = "retained" });
            if (valid)
            {
                insert();
                db.GetCollection(name).FindById(1)["value"].AsString.Should().Be("retained");
                db.GetCollectionNames().Should().Equal(name);
            }
            else
            {
                insert.Should().Throw<LiteException>().Which.ErrorCode.Should().Be(LiteException.INVALID_COLLECTION_NAME);
                db.GetCollectionNames().Should().BeEmpty();
                db.GetCollection("valid2").Insert(new BsonDocument { ["_id"] = 2 });
                db.GetCollection("valid2").Count().Should().Be(1);
            }
        }

        [Fact]
        public void Initial_digit_error_explains_the_position_rule_and_preserves_committed_data()
        {
            const string invalidName = "2e4e2069_da7c_4dbd_8c11_a5ac31041079";
            LiteException failure;
            using var file = new TempFile();
            using (var db = new LiteDatabase(file.Filename))
            {
                db.GetCollection("valid2").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "before" });
                failure = Record.Exception(() => db.GetCollection(invalidName)
                    .Insert(new BsonDocument { ["_id"] = 2, ["value"] = "rejected" }))
                    .Should().BeOfType<LiteException>().Subject;
                failure.ErrorCode.Should().Be(LiteException.INVALID_COLLECTION_NAME);
                db.GetCollectionNames().Should().Equal("valid2");
                db.GetCollection("valid2").Insert(new BsonDocument { ["_id"] = 3, ["value"] = "after" });
                db.Checkpoint();
            }
            using (var reopened = new LiteDatabase(file.Filename))
            {
                reopened.GetCollectionNames().Should().Equal("valid2");
                var rows = reopened.GetCollection("valid2");
                rows.Count().Should().Be(2);
                rows.FindById(1)["value"].AsString.Should().Be("before");
                rows.FindById(3)["value"].AsString.Should().Be("after");
                ((object)rows.FindById(2)).Should().BeNull();
            }

            failure.Message.Should().Contain(invalidName);
            // Accept either an explicit initial-digit prohibition or an explanation
            // of the required initial letter. A character whitelist omits the rule.
            failure.Message.Should().MatchRegex(
                @"(?i)(?:(?:cannot|can't|must not|may not)\s+(?:start|begin).{0,40}(?:digit|number|0-9)|" +
                @"(?:must|should)\s+(?:start|begin).{0,40}(?:letter|alphabetic)|" +
                @"(?:first|initial)\s+character.{0,30}(?:cannot|must not|may not).{0,30}(?:digit|number|0-9))",
                "the diagnostic must explain the initial-digit restriction, not only list allowed characters");
        }
    }
}
