using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Document
{
    public class Case_Insensitive_Tests
    {
        [Fact]
        public void Get_Document_Fields_Case_Insensitive()
        {
            var doc = new BsonDocument
            {
                ["_id"] = 10,
                ["name"] = "John",
                ["Last Job This Year"] = "admin"
            };

            doc["_id"].AsInt32.Should().Be(10);
            doc["_ID"].AsInt32.Should().Be(10);
            doc["_Id"].AsInt32.Should().Be(10);

            doc["name"].AsString.Should().Be("John");
            doc["Name"].AsString.Should().Be("John");
            doc["NamE"].AsString.Should().Be("John");

            doc["Last Job This Year"].AsString.Should().Be("admin");
            doc["last JOB this YEAR"].AsString.Should().Be("admin");

            // using expr
            BsonExpression.Create("$.['Last Job This Year']").Execute(doc).First().AsString.Should().Be("admin");
            BsonExpression.Create("$.['Last JOB THIS Year']").Execute(doc).First().AsString.Should().Be("admin");
        }
    }
}