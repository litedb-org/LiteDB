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
    }
}
